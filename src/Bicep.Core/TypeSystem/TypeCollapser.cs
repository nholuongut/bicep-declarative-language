// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System.Collections.Concurrent;
using Bicep.Core.Extensions;
using Bicep.Core.TypeSystem.Types;

namespace Bicep.Core.TypeSystem;

internal static class TypeCollapser
{
    internal static TypeSymbol? TryCollapse(TypeSymbol type) => type switch
    {
        // it doesn't really make sense to collapse 'never' or 'any'
        _ when type.TypeKind == TypeKind.Never => null,
        AnyType => null,
        UnionType unionType => TryCollapse(unionType),
        _ => type,
    };

    /// <remarks>
    /// How and whether multiple types can be collapsed varies by type, so a finite state machine is used so that each
    /// type can define its collapsing rules separately and we can choose which ruleset to use based on the first 1-2
    /// union members.
    /// </remarks>
    private static TypeSymbol? TryCollapse(UnionType unionType)
    {
        var collapseState = UnionCollapseState.Initialize();
        foreach (var member in Flatten(unionType))
        {
            collapseState = collapseState.Push(member);
        }

        return collapseState.TryCollapse();
    }

    private static IEnumerable<TypeSymbol> Flatten(ITypeReference type) => type.Type switch
    {
        UnionType union => union.Members.SelectMany(Flatten),
        TypeSymbol otherwise => otherwise.AsEnumerable(),
    };

    private interface UnionCollapseState
    {
        UnionCollapseState Push(ITypeReference memberType);

        TypeSymbol? TryCollapse();

        public static UnionCollapseState Initialize() => new InitialState();

        private static TypeSymbol CreateTypeUnion(IEnumerable<TypeSymbol> toUnion, bool nullable)
            => TypeHelper.CreateTypeUnion(nullable ? toUnion.Append(LanguageConstants.Null) : toUnion);

        private class InitialState : UnionCollapseState
        {
            private bool nullable = false;

            public TypeSymbol? TryCollapse() => LanguageConstants.Never;

            public UnionCollapseState Push(ITypeReference memberType) => memberType.Type switch
            {
                NullType => WithNullability(),
                StringLiteralType stringLiteral => new StringCollapse(stringLiteral, nullable),
                StringType @string => new StringCollapse(@string, nullable),
                IntegerLiteralType intLiteral => new IntCollapse(intLiteral, nullable),
                IntegerType @int => new IntCollapse(@int, nullable),
                BooleanLiteralType boolLiteral => new BoolCollapse(boolLiteral, nullable),
                BooleanType @bool => new BoolCollapse(@bool, nullable),
                TupleType tuple => new ArrayCollapse(tuple, nullable),
                ArrayType array => new ArrayCollapse(array, nullable),
                ObjectType @object => new ObjectCollapse(nullable).Push(@object),
                DiscriminatedObjectType discriminatedObjectType => new ObjectCollapse(nullable).Push(discriminatedObjectType),
                AnyType => CollapsesToAny.Instance,
                _ => Uncollapsable.Instance,
            };

            private UnionCollapseState WithNullability()
            {
                nullable = true;
                return this;
            }
        }

        private class StringCollapse : UnionCollapseState
        {
            private readonly RefinementSpanCollapser spanCollapser = new();
            private readonly HashSet<StringLiteralType> stringLiterals = new();
            private bool nullable;

            internal StringCollapse(StringLiteralType stringLiteral, bool nullable)
            {
                stringLiterals.Add(stringLiteral);
                this.nullable = nullable;
            }

            internal StringCollapse(StringType @string, bool nullable)
            {
                spanCollapser.PushSpan(RefinementSpan.For(@string));
                this.nullable = nullable;
            }

            public TypeSymbol? TryCollapse() => CreateTypeUnion(
                // only keep string literals that are not valid in any of the discrete spans
                stringLiterals.Where(literal => !spanCollapser.Spans.Any(s => s.Contains(literal.RawStringValue.Length)))
                    // create a refined string type for each span
                    .Concat(spanCollapser.Spans.Select(span => TypeFactory.CreateStringType(
                        span.Min switch
                        {
                            <= 0 => null,
                            long otherwise => otherwise,
                        },
                        span.Max switch
                        {
                            long.MaxValue => null,
                            long otherwise => otherwise,
                        },
                        span.Flags))),
                nullable);

            public UnionCollapseState Push(ITypeReference memberType)
            {
                switch (memberType.Type)
                {
                    case StringLiteralType literal:
                        stringLiterals.Add(literal);
                        return this;
                    case StringType @string:
                        spanCollapser.PushSpan(RefinementSpan.For(@string));
                        return this;
                    case NullType:
                        nullable = true;
                        return this;
                    case AnyType:
                        return CollapsesToAny.Instance;
                    default:
                        return Uncollapsable.Instance;
                }
            }
        }

        private class IntCollapse : UnionCollapseState
        {
            private readonly RefinementSpanCollapser spanCollapser = new();
            private bool nullable;

            internal IntCollapse(IntegerLiteralType integerLiteral, bool nullable)
            {
                spanCollapser.PushSpan(RefinementSpan.For(integerLiteral));
                this.nullable = nullable;
            }

            internal IntCollapse(IntegerType @int, bool nullable)
            {
                spanCollapser.PushSpan(RefinementSpan.For(@int));
                this.nullable = nullable;
            }

            public TypeSymbol? TryCollapse() => CreateTypeUnion(
                spanCollapser.Spans.Select(span => span.Min == span.Max
                    ? TypeFactory.CreateIntegerLiteralType(span.Min, span.Flags)
                    : TypeFactory.CreateIntegerType(
                        span.Min switch
                        {
                            long.MinValue => null,
                            long otherwise => otherwise,
                        },
                        span.Max switch
                        {
                            long.MaxValue => null,
                            long otherwise => otherwise,
                        },
                        span.Flags)),
                nullable);

            public UnionCollapseState Push(ITypeReference memberType)
            {
                switch (memberType.Type)
                {
                    case IntegerLiteralType literal:
                        spanCollapser.PushSpan(RefinementSpan.For(literal));
                        return this;
                    case IntegerType @int:
                        spanCollapser.PushSpan(RefinementSpan.For(@int));
                        return this;
                    case NullType:
                        nullable = true;
                        return this;
                    case AnyType:
                        return CollapsesToAny.Instance;
                    default:
                        return Uncollapsable.Instance;
                }
            }
        }

        private class BoolCollapse : UnionCollapseState
        {
            private bool includesTrue;
            private bool includesFalse;
            private TypeSymbolValidationFlags flags;
            private bool nullable;

            internal BoolCollapse(BooleanLiteralType literal, bool nullable)
            {
                includesTrue = literal.Value;
                includesFalse = !literal.Value;
                flags = literal.ValidationFlags;
                this.nullable = nullable;
            }

            internal BoolCollapse(BooleanType @bool, bool nullable)
            {
                includesTrue = includesFalse = true;
                flags = @bool.ValidationFlags;
                this.nullable = nullable;
            }

            public TypeSymbol? TryCollapse()
            {
                TypeSymbol collapsed = includesTrue ^ includesFalse
                    ? TypeFactory.CreateBooleanLiteralType(includesTrue, flags)
                    : TypeFactory.CreateBooleanType(flags);

                return nullable ? TypeHelper.CreateTypeUnion(new[] { collapsed, LanguageConstants.Null }) : collapsed;
            }

            public UnionCollapseState Push(ITypeReference memberType)
            {
                switch (memberType.Type)
                {
                    case BooleanLiteralType literal:
                        if (literal.Value)
                        {
                            includesTrue = true;
                        }
                        else
                        {
                            includesFalse = true;
                        }
                        flags |= literal.ValidationFlags;
                        return this;
                    case BooleanType @bool:
                        includesTrue = true;
                        includesFalse = true;
                        flags |= @bool.ValidationFlags;
                        return this;
                    case NullType:
                        nullable = true;
                        return this;
                    case AnyType:
                        return CollapsesToAny.Instance;
                    default:
                        return Uncollapsable.Instance;
                }
            }
        }

        private class ArrayCollapse : UnionCollapseState
        {
            private readonly ConcurrentDictionary<TypeSymbol, RefinementSpanCollapser> spanCollapsesByItemType = new();
            private readonly HashSet<TupleType> tuples = new();
            private bool nullable;

            internal ArrayCollapse(TupleType tuple, bool nullable)
            {
                PushTuple(tuple);
                this.nullable = nullable;
            }

            internal ArrayCollapse(ArrayType array, bool nullable)
            {
                PushArraySpan(array);
                this.nullable = nullable;
            }

            public TypeSymbol? TryCollapse()
            {
                foreach (var tuple in tuples.ToArray())
                {
                    if (spanCollapsesByItemType.Any(kvp => kvp.Value.Spans.Any(span => span.Min <= tuple.Items.Length && tuple.Items.Length <= span.Max) &&
                        TypeValidator.AreTypesAssignable(tuple.Item.Type, kvp.Key)))
                    {
                        tuples.Remove(tuple);
                    }
                }

                return CreateTypeUnion(
                    tuples.Concat(spanCollapsesByItemType.SelectMany(kvp => kvp.Value.Spans.Select(span => TypeFactory.CreateArrayType(kvp.Key,
                        span.Min switch
                        {
                            <= 0 => null,
                            long otherwise => otherwise,
                        },
                        span.Max switch
                        {
                            long.MaxValue => null,
                            long otherwise => otherwise,
                        },
                        span.Flags)))),
                    nullable);
            }

            public UnionCollapseState Push(ITypeReference memberType)
            {
                switch (memberType.Type)
                {
                    case TupleType tuple:
                        PushTuple(tuple);
                        return this;
                    case ArrayType array:
                        PushArraySpan(array);
                        return this;
                    case NullType:
                        nullable = true;
                        return this;
                    case AnyType:
                        return CollapsesToAny.Instance;
                    default:
                        return Uncollapsable.Instance;
                }
            }

            private void PushTuple(TupleType tuple)
            {
                if (tuple.Items.Select(i => i.Type).Distinct().Count() == 1)
                {
                    PushArraySpan(TypeFactory.CreateArrayType(tuple.Items[0], tuple.Items.Length, tuple.Items.Length, tuple.ValidationFlags));
                }
                else
                {
                    tuples.Add(tuple);
                }
            }

            private void PushArraySpan(ArrayType array)
                => spanCollapsesByItemType.GetOrAdd(array.Item.Type, _ => new RefinementSpanCollapser()).PushSpan(RefinementSpan.For(array));
        }

        private class ObjectCollapse : UnionCollapseState
        {
            private readonly DiscriminatedObjectTypeBuilder discriminatedObjectTypeBuilder = new();
            private TypeSymbolValidationFlags flags = TypeSymbolValidationFlags.Default;
            private bool nullable;

            internal ObjectCollapse(bool nullable)
            {
                this.nullable = nullable;
            }

            public TypeSymbol? TryCollapse()
            {
                var (members, viableDiscriminators) = discriminatedObjectTypeBuilder.Build();

                if (members.Count == 1)
                {
                    return nullable ? TypeHelper.CreateTypeUnion(members.Single(), LanguageConstants.Null) : members.Single();
                }

                var discriminator = viableDiscriminators
                    .OrderBy(possibleDiscriminator =>
                    {
                        var index = LanguageConstants.DiscriminatorPreferenceOrder.IndexOf(possibleDiscriminator);

                        return index > -1 ? index : LanguageConstants.DiscriminatorPreferenceOrder.Length;
                    })
                    .ThenBy(d => d)
                    .FirstOrDefault();

                if (discriminator is not null)
                {
                    var baseType = new DiscriminatedObjectType(string.Join(" | ", TypeHelper.GetOrderedTypeNames(members)), flags, discriminator, members);
                    return nullable ? TypeHelper.CreateTypeUnion(baseType, LanguageConstants.Null) : baseType;
                }

                ObjectTypeNameBuilder structuralNameBuilder = new();
                List<TypeProperty> properties = new();
                foreach (var declaredPropertyName in members.SelectMany(m => m.Properties.Keys).Distinct()
                    .OrderBy(x => x, LanguageConstants.IdentifierComparer))
                {
                    List<TypeSymbol> possibleTypes = new();
                    TypePropertyFlags propertyFlags = ~TypePropertyFlags.None;
                    foreach (var member in members)
                    {
                        if (member.Properties.TryGetValue(declaredPropertyName, out var property))
                        {
                            possibleTypes.Add(property.TypeReference.Type);
                            propertyFlags &= property.Flags;
                        }
                        else
                        {
                            // If a property is not declared on a member, then it may not be present on the resultant
                            // value. Make sure it is not flagged as required.
                            propertyFlags &= ~TypePropertyFlags.Required;

                            if (member.AdditionalPropertiesType?.Type is { } addlPropertiesType)
                            {
                                possibleTypes.Add(addlPropertiesType);
                            }
                            propertyFlags &= member.AdditionalPropertiesType is not null
                                ? member.AdditionalPropertiesFlags
                                : TypePropertyFlags.FallbackProperty;
                        }
                    }

                    var propertyTypeUnion = TypeHelper.CreateTypeUnion(possibleTypes);

                    properties.Add(new(
                        declaredPropertyName,
                        TypeCollapser.TryCollapse(propertyTypeUnion) is { } collapsed ? collapsed : propertyTypeUnion,
                        propertyFlags));
                    structuralNameBuilder.AppendProperty(declaredPropertyName, properties[^1].TypeReference.Type.Name);
                }

                var (additionalPropertiesType, additionalPropertiesFlags) = GetAdditionalPropertiesType(members);
                if (additionalPropertiesType is not null &&
                    !additionalPropertiesFlags.HasFlag(TypePropertyFlags.FallbackProperty))
                {
                    structuralNameBuilder.AppendPropertyMatcher(additionalPropertiesType.Name);
                }

                return new ObjectType(
                    structuralNameBuilder.ToString(),
                    flags,
                    properties,
                    additionalPropertiesType,
                    additionalPropertiesFlags);
            }

            private static (TypeSymbol? type, TypePropertyFlags flags) GetAdditionalPropertiesType(
                IEnumerable<ObjectType> objects)
            {
                var noneHaveAdditionalPropertiesType = true;
                var anyHaveNullAdditionalPropertiesType = false;
                var allHaveImplicitAnyAdditionalPropertiesType = true;

                List<TypeSymbol> possibleTypes = new();
                TypePropertyFlags propertyFlags = ~TypePropertyFlags.None;
                foreach (var @object in objects)
                {
                    noneHaveAdditionalPropertiesType &= @object.AdditionalPropertiesType is null;
                    anyHaveNullAdditionalPropertiesType |= @object.AdditionalPropertiesType is null;
                    allHaveImplicitAnyAdditionalPropertiesType &= @object.AdditionalPropertiesType is not null &&
                        @object.HasExplicitAdditionalPropertiesType;

                    if (@object.AdditionalPropertiesType?.Type is { } addlPropertiesType)
                    {
                        possibleTypes.Add(addlPropertiesType);
                    }
                    propertyFlags &= @object.AdditionalPropertiesFlags;
                }

                if (noneHaveAdditionalPropertiesType)
                {
                    return (null, TypePropertyFlags.None);
                }

                if (allHaveImplicitAnyAdditionalPropertiesType)
                {
                    return (LanguageConstants.Any, TypePropertyFlags.FallbackProperty);
                }

                if (anyHaveNullAdditionalPropertiesType)
                {
                    propertyFlags |= TypePropertyFlags.FallbackProperty;
                }

                return (TypeHelper.CollapseOrCreateTypeUnion(possibleTypes), propertyFlags);
            }

            public UnionCollapseState Push(ITypeReference memberType)
            {
                switch (memberType.Type)
                {
                    case ObjectType @object:
                        flags |= @object.ValidationFlags;
                        discriminatedObjectTypeBuilder.TryInclude(@object);
                        return this;
                    case DiscriminatedObjectType @union:
                        flags |= @union.ValidationFlags;
                        foreach (var member in union.UnionMembersByKey.Values)
                        {
                            Push(member);
                        }
                        return this;
                    case NullType:
                        nullable = true;
                        return this;
                    case AnyType:
                        return CollapsesToAny.Instance;
                    default:
                        return Uncollapsable.Instance;
                }
            }
        }

        private class CollapsesToAny : UnionCollapseState
        {
            private CollapsesToAny() { }

            internal static readonly CollapsesToAny Instance = new();

            public TypeSymbol? TryCollapse() => LanguageConstants.Any;

            public UnionCollapseState Push(ITypeReference _) => this;
        }

        private class Uncollapsable : UnionCollapseState
        {
            private Uncollapsable() { }

            internal static readonly Uncollapsable Instance = new();

            public TypeSymbol? TryCollapse() => null;

            public UnionCollapseState Push(ITypeReference _) => this;
        }

        private readonly record struct RefinementSpan(long Min, long Max, TypeSymbolValidationFlags Flags)
        {
            internal bool Contains(long number) => Min <= number && number <= Max;

            internal bool OverlapsOrAbuts(RefinementSpan other) => Min <= (other.Max == long.MaxValue ? long.MaxValue : other.Max + 1) &&
                Max >= (other.Min == long.MinValue ? long.MinValue : other.Min - 1);

            internal RefinementSpan Fuse(RefinementSpan other) => new(Math.Min(Min, other.Min), Math.Max(Max, other.Max), Flags | other.Flags);

            internal static RefinementSpan For(StringType @string) => new(@string.MinLength ?? 0, @string.MaxLength ?? long.MaxValue, @string.ValidationFlags);

            internal static RefinementSpan For(IntegerType @int) => new(@int.MinValue ?? long.MinValue, @int.MaxValue ?? long.MaxValue, @int.ValidationFlags);

            internal static RefinementSpan For(IntegerLiteralType literal) => new(literal.Value, literal.Value, literal.ValidationFlags);

            internal static RefinementSpan For(ArrayType array) => new(array.MinLength ?? 0, array.MaxLength ?? long.MaxValue, array.ValidationFlags);
        }

        private class RefinementSpanCollapser
        {
            private readonly List<RefinementSpan> discreteSpans = new();

            internal void PushSpan(RefinementSpan span)
            {
                for (int i = 0; i < discreteSpans.Count; i++)
                {
                    if (span.OverlapsOrAbuts(discreteSpans[i]))
                    {
                        discreteSpans[i] = discreteSpans[i].Fuse(span);
                        if (discreteSpans.Count > 1)
                        {
                            var fused = discreteSpans[i];
                            discreteSpans.RemoveAt(i);
                            PushSpan(fused);
                        }
                        return;
                    }
                }

                discreteSpans.Add(span);
            }

            internal IEnumerable<RefinementSpan> Spans => discreteSpans;
        }
    }
}
