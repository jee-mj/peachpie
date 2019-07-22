﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Pchp.CodeAnalysis.Semantics;
using Pchp.CodeAnalysis.Semantics.Graph;
using Pchp.CodeAnalysis.Symbols;
using Peachpie.CodeAnalysis.Utilities;
using Ast = Devsense.PHP.Syntax.Ast;

namespace Pchp.CodeAnalysis.FlowAnalysis.Passes
{
    internal class TransformationRewriter : GraphRewriter
    {
        private readonly DelayedTransformations _delayedTransformations;
        private readonly SourceRoutineSymbol _routine;

        protected PhpCompilation DeclaringCompilation => _routine.DeclaringCompilation;
        protected BoundTypeRefFactory BoundTypeRefFactory => DeclaringCompilation.TypeRefFactory;

        public int TransformationCount { get; private set; }

        public static bool TryTransform(DelayedTransformations delayedTransformations, SourceRoutineSymbol routine)
        {
            if (routine.ControlFlowGraph == null)
            {
                // abstract method
                return false;
            }

            //
            var rewriter = new TransformationRewriter(delayedTransformations, routine);
            var currentCFG = routine.ControlFlowGraph;
            var updatedCFG = (ControlFlowGraph)rewriter.VisitCFG(currentCFG);

            routine.ControlFlowGraph = updatedCFG;

            Debug.Assert((rewriter.TransformationCount != 0) == (updatedCFG != currentCFG)); // transformations <=> cfg updated                                                                                 //
            return updatedCFG != currentCFG;
        }

        private TransformationRewriter(DelayedTransformations delayedTransformations, SourceRoutineSymbol routine)
        {
            _delayedTransformations = delayedTransformations;
            _routine = routine ?? throw ExceptionUtilities.ArgumentNull(nameof(routine));
        }

        protected override void OnVisitCFG(ControlFlowGraph x)
        {
            Debug.Assert(_routine.ControlFlowGraph == x);
        }

        private protected override void OnUnreachableRoutineFound(SourceRoutineSymbol routine)
        {
            _delayedTransformations.UnreachableRoutines.Add(routine);
        }

        private protected override void OnUnreachableTypeFound(SourceTypeSymbol type)
        {
            _delayedTransformations.UnreachableTypes.Add(type);
        }

        public override object VisitConditional(BoundConditionalEx x)
        {
            x = (BoundConditionalEx)base.VisitConditional(x);

            if (x.IfTrue != null) // otherwise it is (A ?: B) operator
            {
                if (x.Condition.ConstantValue.TryConvertToBool(out var condVal))
                {
                    TransformationCount++;
                    return (condVal ? x.IfTrue : x.IfFalse).WithAccess(x);
                }

                if (x.IfTrue.ConstantValue.IsBool(out bool trueVal) &&
                    x.IfFalse.ConstantValue.IsBool(out bool falseVal))
                {
                    if (trueVal && !falseVal)
                    {
                        // A ? true : false => (bool)A
                        TransformationCount++;
                        return new BoundConversionEx(x.Condition, BoundTypeRefFactory.BoolTypeRef).WithAccess(x);
                    }
                    else if (!trueVal && falseVal)
                    {
                        // A ? false : true => !A
                        TransformationCount++;
                        return new BoundUnaryEx(x.Condition, Ast.Operations.LogicNegation).WithAccess(x);
                    }
                }

                // handled in BoundConditionalEx.Emit:
                //// !COND ? A : B => COND ? B : A
                //if (x.Condition is BoundUnaryEx unary && unary.Operation == Ast.Operations.LogicNegation)
                //{
                //    TransformationCount++;
                //    return new BoundConditionalEx(unary.Operand, x.IfFalse, x.IfTrue).WithAccess(x);
                //}
            }

            return x;
        }

        public override object VisitBinaryExpression(BoundBinaryEx x)
        {
            // AND, OR:
            if (x.Operation == Ast.Operations.And ||
                x.Operation == Ast.Operations.Or)
            {
                if (x.Left.ConstantValue.TryConvertToBool(out var bleft))
                {
                    if (x.Operation == Ast.Operations.And)
                    {
                        TransformationCount++;
                        // TRUE && Right => Right
                        // FALSE && Right => FALSE
                        return bleft ? x.Right : x.Left;
                    }
                    else if (x.Operation == Ast.Operations.Or)
                    {
                        TransformationCount++;
                        // TRUE || Right => TRUE
                        // FALSE || Right => Right
                        return bleft ? x.Left : x.Right;
                    }
                }

                if (x.Right.ConstantValue.TryConvertToBool(out var bright))
                {
                    if (x.Operation == Ast.Operations.And && bright == true)
                    {
                        TransformationCount++;
                        return x.Left; // Left && TRUE => Left
                    }
                    else if (x.Operation == Ast.Operations.Or && bright == false)
                    {
                        TransformationCount++;
                        // Left || FALSE => Left
                        return x.Left;
                    }
                }
            }

            //
            return base.VisitBinaryExpression(x);
        }

        public override object VisitUnaryExpression(BoundUnaryEx x)
        {
            if (x.Operation == Ast.Operations.LogicNegation &&
                x.Operand is BoundUnaryEx ux &&
                ux.Operation == Ast.Operations.LogicNegation)
            {
                // !!X -> (bool)X
                TransformationCount++;
                return new BoundConversionEx((BoundExpression)Accept(ux.Operand), BoundTypeRefFactory.BoolTypeRef).WithAccess(x.Access);
            }

            return base.VisitUnaryExpression(x);
        }

        public override object VisitCopyValue(BoundCopyValue x)
        {
            var valueEx = (BoundExpression)Accept(x.Expression);
            if (valueEx.IsDeeplyCopied)
            {
                return x.Update(valueEx);
            }
            else
            {
                // deep copy is unnecessary:
                TransformationCount++;
                return valueEx;
            }
        }

        public override object VisitAssign(BoundAssignEx x)
        {
            // A = A <binOp> <right>
            if (x.Target is BoundVariableRef trg
                && MatchExprSkipCopy(x.Value, out BoundBinaryEx binOp, isCopied: out _)
                && binOp.Left is BoundVariableRef valLeft
                && trg.Variable == valLeft.Variable)
            {
                var newTrg =
                    new BoundVariableRef(trg.Name)
                    .WithAccess(trg.Access.WithRead())
                    .WithSyntax(trg.PhpSyntax);

                // A = A +/- 1; => ++A; / --A;
                if ((binOp.Operation == Ast.Operations.Add || binOp.Operation == Ast.Operations.Sub)
                    && binOp.Right.ConstantValue.IsInteger(out long rightVal) && rightVal == 1)
                {
                    TransformationCount++;
                    return new BoundIncDecEx(newTrg, binOp.Operation == Ast.Operations.Add, false).WithAccess(x);
                }

                // A = A & B => A &= B; // &, |, ^, <<, >>, +, -, *, /, %, **, .
                switch (binOp.Operation)
                {
                    case Ast.Operations.BitAnd:
                    case Ast.Operations.BitOr:
                    case Ast.Operations.BitXor:
                    case Ast.Operations.ShiftLeft:
                    case Ast.Operations.ShiftRight:
                    case Ast.Operations.Add:
                    case Ast.Operations.Sub:
                    case Ast.Operations.Mul:
                    case Ast.Operations.Div:
                    case Ast.Operations.Mod:
                    case Ast.Operations.Pow:
                    case Ast.Operations.Concat:
                        TransformationCount++;
                        var compoundOp = AstUtils.BinaryToCompoundOp(binOp.Operation);
                        return new BoundCompoundAssignEx(newTrg, binOp.Right, compoundOp).WithAccess(x);
                }
            }

            return base.VisitAssign(x);
        }

        public override object VisitCFGConditionalEdge(ConditionalEdge x)
        {
            if (x.Condition.ConstantValue.TryConvertToBool(out bool condValue))
            {
                TransformationCount++;
                NotePossiblyUnreachable(condValue ? x.FalseTarget : x.TrueTarget);
                var target = condValue ? x.TrueTarget : x.FalseTarget;
                return new SimpleEdge((BoundBlock)Accept(target));
            }

            if (x.Condition is BoundBinaryEx bex)
            {
                // if (A && FALSE)
                if (bex.Operation == Ast.Operations.And && bex.Right.ConstantValue.TryConvertToBool(out var bright) && bright == false)
                {
                    // if (Left && FALSE) {Unreachable} else {F} -> if (Left) {F} else {F}
                    // result is always FALSE but we have to evaluate Left
                    TransformationCount++;
                    NotePossiblyUnreachable(x.TrueTarget);

                    var target = (BoundBlock)Accept(x.FalseTarget);
                    return new ConditionalEdge(target, target, bex.Left.WithAccess(BoundAccess.None));
                }

                // if (A || TRUE)
                if (bex.Operation == Ast.Operations.Or && bex.Right.ConstantValue.TryConvertToBool(out bright) && bright == true)
                {
                    // if (Left || TRUE) {T} else {Unreachable} -> if (Left) {T} else {T}
                    // result is always FALSE but we have to evaluate Left
                    TransformationCount++;
                    NotePossiblyUnreachable(x.FalseTarget);

                    var target = (BoundBlock)Accept(x.TrueTarget);
                    return new ConditionalEdge(target, target, bex.Left.WithAccess(BoundAccess.None));
                }
            }

            //
            return base.VisitCFGConditionalEdge(x);
        }

        public override object VisitGlobalFunctionCall(BoundGlobalFunctionCall x)
        {
            // TODO: extensible, dictionary of functions ?

            if (x.Name.NameValue == NameUtils.SpecialNames.dirname)
            {
                // dirname( __FILE__ ) -> __DIR__
                if (x.ArgumentsInSourceOrder.Length == 1 &&
                    x.ArgumentsInSourceOrder[0].Value is BoundPseudoConst pc &&
                    pc.ConstType == Ast.PseudoConstUse.Types.File)
                {
                    TransformationCount++;
                    return new BoundPseudoConst(Ast.PseudoConstUse.Types.Dir).WithAccess(x.Access);
                }
            }
            else if (x.Name.NameValue == NameUtils.SpecialNames.get_parent_class)
            {
                bool TryResolveParentClassInCurrentClassContext(SourceRoutineSymbol routine, out BoundLiteral newExpression)
                {
                    // in global function, always FALSE
                    if (routine is SourceFunctionSymbol)
                    {
                        // FALSE
                        newExpression = new BoundLiteral(false.AsObject());
                        return true;
                    }

                    // in a method, we can resolve in compile time:
                    if (routine is SourceMethodSymbol m && m.ContainingType is SourceTypeSymbol t && !t.IsTrait)
                    {
                        if (t.BaseType == null || t.BaseType.IsObjectType())
                        {
                            // FALSE
                            newExpression = new BoundLiteral(false.AsObject())
                            {
                                ConstantValue = false.AsOptional()
                            };
                            return true;
                        }
                        else
                        {
                            // {class name}
                            var baseTypeName = t.BaseType.PhpQualifiedName().ToString();
                            newExpression = new BoundLiteral(baseTypeName)
                            {
                                ConstantValue = baseTypeName
                            };
                            return true;
                        }
                    }

                    //
                    newExpression = default;
                    return false;
                }

                // get_parent_class() -> {class name} | FALSE
                if (x.ArgumentsInSourceOrder.Length == 0)
                {
                    if (TryResolveParentClassInCurrentClassContext(_routine, out var newExpression))
                    {
                        TransformationCount++;
                        return newExpression.WithContext(x);
                    }
                }

                // get_parent_class( ??? ) -> parent::class | FALSE
                if (x.ArgumentsInSourceOrder.Length == 1)
                {
                    // get_parent_class($this), get_parent_class(__CLASS__) ->  {class name} | FALSE
                    if ((x.ArgumentsInSourceOrder[0].Value is BoundVariableRef varref && varref.Variable is ThisVariableReference) ||
                        (x.ArgumentsInSourceOrder[0].Value is BoundPseudoConst pc && pc.ConstType == Ast.PseudoConstUse.Types.Class))
                    {
                        if (TryResolveParentClassInCurrentClassContext(_routine, out var newExpression))
                        {
                            TransformationCount++;
                            return newExpression.WithContext(x);
                        }
                    }
                }

            }
            else if (   // method_exists(class_name, method_name) -> FALSE
                x.Name.NameValue == NameUtils.SpecialNames.method_exists &&
                x.ArgumentsInSourceOrder.Length == 2)
            {
                // method_exists(FALSE, ...) -> FALSE
                var value = x.ArgumentsInSourceOrder[0].Value.ConstantValue;
                if (value.HasValue && value.TryConvertToBool(out var bvalue) && !bvalue)
                {
                    TransformationCount++;
                    return new BoundLiteral(false.AsObject())
                    {
                        ConstantValue = false.AsOptional()
                    }.WithContext(x);
                }
            }
            else if (   // ini_get( {svalue} ) : string|FALSE
                x.Name.NameValue == NameUtils.SpecialNames.ini_get &&
                x.ArgumentsInSourceOrder.Length == 1 &&
                x.ArgumentsInSourceOrder[0].Value.ConstantValue.TryConvertToString(out var svalue))
            {
                // options we're not supporting for sure
                // always FALSE
                if (svalue.StartsWith("xdebug.") || svalue.StartsWith("xcache.") || svalue.StartsWith("opcache.") || svalue.StartsWith("apc."))
                {
                    TransformationCount++;
                    return new BoundLiteral(false.AsObject())
                    {
                        ConstantValue = false.AsOptional()
                    }.WithContext(x);

                    // TODO: well-known ini options can be translated to access the configuration property directly
                }
            }
            else if (   // extension_loaded( {ext_name} ) : bool
                x.Name.NameValue == NameUtils.SpecialNames.extension_loaded &&
                x.ArgumentsInSourceOrder.Length == 1 &&
                x.ArgumentsInSourceOrder[0].Value.ConstantValue.TryConvertToString(out var ext_name))
            {
                TransformationCount++;

                bool hasextension = DeclaringCompilation
                    .GlobalSemantics
                    .Extensions
                    .Contains(ext_name, StringComparer.OrdinalIgnoreCase);

                // CONSIDER: only when hasextension == True ? Can we add extensions in runtime ?

                Trace.WriteLine($"'extension_loaded({ext_name})' evaluated to {hasextension}.");

                return new BoundLiteral(hasextension.AsObject())
                {
                    ConstantValue = hasextension.AsOptional()
                }.WithContext(x);
            }

            //
            return base.VisitGlobalFunctionCall(x);
        }

        public override object VisitConcat(BoundConcatEx x)
        {
            // transform arguments first:
            x = (BoundConcatEx)base.VisitConcat(x);

            //
            var args = x.ArgumentsInSourceOrder;
            if (args.Length == 0 || args.All(IsEmptyString))
            {
                // empty string:
                TransformationCount++;
                return new BoundLiteral(string.Empty) { ConstantValue = new Optional<object>(string.Empty) }.WithContext(x);
            }

            // visit & concat in compile time if we can:
            var newargs = args;
            int i = 0;
            do
            {
                // accumulate evaluated string value if possible:
                if (newargs[i].Value.ConstantValue.TryConvertToString(out var value))
                {
                    string result = value;
                    int end = i + 1;
                    while (end < newargs.Length && newargs[end].Value.ConstantValue.TryConvertToString(out var tmp))
                    {
                        result += tmp;
                        end++;
                    }

                    if (end > i + 1) // we concat'ed something!
                    {
                        newargs = newargs.RemoveRange(i, end - i);

                        if (!string.IsNullOrEmpty(result))
                        {
                            newargs = newargs.Insert(i, BoundArgument.Create(new BoundLiteral(result)
                            {
                                ConstantValue = new Optional<object>(result),
                                TypeRefMask = _routine.TypeRefContext.GetStringTypeMask(),
                                ResultType = DeclaringCompilation.CoreTypes.String,
                            }.WithAccess(BoundAccess.Read)));
                        }
                    }
                }

                //
                i++;
            } while (i < newargs.Length);

            //
            if (newargs != args)
            {
                TransformationCount++;

                if (newargs.Length == 0)
                {
                    return new BoundLiteral(string.Empty) { ConstantValue = new Optional<object>(string.Empty) }.WithContext(x);
                }
                else if (newargs.Length == 1 && newargs[0].Value.ConstantValue.TryConvertToString(out var value))
                {
                    // "value"
                    return new BoundLiteral(value) { ConstantValue = new Optional<object>(value) }.WithContext(x);
                }

                //
                return x.Update(newargs);
            }

            //
            return x;
        }

        /// <summary>
        /// If <paramref name="expr"/> is of type <typeparamref name="T"/> or it is a <see cref="BoundCopyValue" /> enclosing an
        /// expression of type <typeparamref name="T"/>, store the expression to <paramref name="typedExpr"/> and return true;
        /// otherwise, return false. Store to <paramref name="isCopied"/> whether <paramref name="typedExpr"/> was enclosed in
        /// <see cref="BoundCopyValue"/>.
        /// </summary>
        private static bool MatchExprSkipCopy<T>(BoundExpression expr, out T typedExpr, out bool isCopied) where T : BoundExpression
        {
            if (expr is T res)
            {
                typedExpr = res;
                isCopied = false;
                return true;
            }
            else if (expr is BoundCopyValue copyVal && copyVal.Expression is T copiedRes)
            {
                typedExpr = copiedRes;
                isCopied = true;
                return true;
            }

            typedExpr = default;
            isCopied = default;
            return false;
        }

        private static bool IsEmptyString(BoundArgument a) => a.Value.ConstantValue.HasValue && ExpressionsExtension.IsEmptyStringValue(a.Value.ConstantValue.Value);
    }
}
