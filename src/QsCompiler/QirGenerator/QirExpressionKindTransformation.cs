﻿using Llvm.NET.DebugInfo;
using Llvm.NET.Instructions;
using Llvm.NET.Types;
using Llvm.NET.Values;
using Microsoft.Quantum.QsCompiler.DataTypes;
using Microsoft.Quantum.QsCompiler.SyntaxTokens;
using Microsoft.Quantum.QsCompiler.SyntaxTree;
using Microsoft.Quantum.QsCompiler.Transformations.Core;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Linq.Expressions;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Transactions;

namespace Microsoft.Quantum.QsCompiler.QirGenerator
{
    using ResolvedExpression = QsExpressionKind<TypedExpression, Identifier, ResolvedType>;
    using ArgumentTuple = QsTuple<LocalVariableDeclaration<QsLocalSymbol>>;
    using QsResolvedTypeKind = QsTypeKind<ResolvedType, UserDefinedType, QsTypeParameter, CallableInformation>;

    public class QirExpressionKindTransformation : ExpressionKindTransformation<GenerationContext>
    {
        #region Constructors
        public QirExpressionKindTransformation(SyntaxTreeTransformation<GenerationContext> parentTransformation) : base(parentTransformation)
        {
        }

        public QirExpressionKindTransformation(GenerationContext sharedState) : base(sharedState)
        {
        }

        public QirExpressionKindTransformation(SyntaxTreeTransformation<GenerationContext> parentTransformation, TransformationOptions options) : base(parentTransformation, options)
        {
        }

        public QirExpressionKindTransformation(GenerationContext sharedState, TransformationOptions options) : base(sharedState, options)
        {
        }
        #endregion

        public override ResolvedExpression OnAddition(TypedExpression lhs, TypedExpression rhs)
        {
            this.ProcessSubexpression(lhs);
            Value lhsValue = this.SharedState.ValueStack.Pop();
            this.ProcessSubexpression(rhs);
            Value rhsValue = this.SharedState.ValueStack.Pop();

            if (lhs.ResolvedType.Resolution.IsInt)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.Add(lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsDouble)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.FAdd(lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsBigInt)
            {
                var adder = this.SharedState.GetRuntimeFunction("bigint_add");
                this.SharedState.PushValueInScope(this.SharedState.CurrentBuilder.Call(adder, lhsValue, rhsValue),
                    lhs.ResolvedType);
            }
            else if (lhs.ResolvedType.Resolution.IsString)
            {
                var adder = this.SharedState.GetRuntimeFunction("string_concatenate");
                this.SharedState.PushValueInScope(this.SharedState.CurrentBuilder.Call(adder, lhsValue, rhsValue),
                    lhs.ResolvedType);
            }
            else if (lhs.ResolvedType.Resolution.IsArrayType)
            {
                var adder = this.SharedState.GetRuntimeFunction("array_concatenate");
                this.SharedState.PushValueInScope(this.SharedState.CurrentBuilder.Call(adder, lhsValue, rhsValue),
                    lhs.ResolvedType);
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(lhsValue.NativeType));
            }
            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnAdjointApplication(TypedExpression ex)
        {
            // ex will evaluate to a callable
            var baseCallable = this.EvaluateSubexpression(ex);

            // If ex was a variable, we need to make a copy before we take the adjoint.
            Value callable;
            
            if ((ex.Expression is ResolvedExpression.Identifier id) && (id.Item1.IsLocalVariable))
            {
                var copier = this.SharedState.GetRuntimeFunction("callable_copy");
                callable = this.SharedState.CurrentBuilder.Call(copier, baseCallable);
                this.SharedState.ScopeMgr.AddValue(callable, ex.ResolvedType);
            }
            else
            {
                callable = baseCallable;
            }

            var adjointer = this.SharedState.GetRuntimeFunction("callable_make_adjoint");
            this.SharedState.CurrentBuilder.Call(adjointer, callable);
            this.SharedState.ValueStack.Push(callable);

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnArrayItem(TypedExpression arr, TypedExpression idx)
        {
            // TODO: handle multi-dimensional arrays
            var array = this.EvaluateSubexpression(arr);
            var index = this.EvaluateSubexpression(idx);

            if (idx.ResolvedType.Resolution.IsInt)
            {
                var pointer = this.SharedState.CurrentBuilder.Call(
                    this.SharedState.GetRuntimeFunction("array_get_element_ptr_1d"), array, index);

                // Get the element type
                var elementType = (arr.ResolvedType.Resolution as QsResolvedTypeKind.ArrayType).Item;
                var elementTypeRef = this.SharedState.LlvmTypeFromQsharpType(elementType);
                var elementPointerTypeRef = elementTypeRef.CreatePointerType();

                // And now fetch the element
                var elementPointer = this.SharedState.CurrentBuilder.BitCast(pointer, elementPointerTypeRef);
                var element = this.SharedState.CurrentBuilder.Load(elementTypeRef, elementPointer);
                this.SharedState.ValueStack.Push(element);
            }
            else
            {
                var slicer = this.SharedState.GetRuntimeFunction("array_slice");
                var slice = this.SharedState.CurrentBuilder.Call(slicer, array, 
                    this.SharedState.CurrentContext.CreateConstant(0), index);
                this.SharedState.PushValueInScope(slice, arr.ResolvedType);
            }

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnBigIntLiteral(BigInteger b)
        {
            Value bigIntValue;
            if ((b <= long.MaxValue) && (b >= long.MinValue))
            {
                var val = this.SharedState.CurrentContext.CreateConstant((long)b);
                var func = this.SharedState.GetRuntimeFunction("bigint_create_i64");
                bigIntValue = this.SharedState.CurrentBuilder.Call(func, val);
            }
            else
            {
                var bytes = b.ToByteArray();
                var n = this.SharedState.CurrentContext.CreateConstant(bytes.Length);
                var byteArray = ConstantArray.From(this.SharedState.CurrentContext.Int8Type,
                    bytes.Select(s => this.SharedState.CurrentContext.CreateConstant(s)).ToArray());
                var zeroByteArray = this.SharedState.CurrentBuilder.BitCast(byteArray,
                    this.SharedState.CurrentContext.Int8Type.CreateArrayType(0));
                var func = this.SharedState.GetRuntimeFunction("bigint_create_array");
                bigIntValue = this.SharedState.CurrentBuilder.Call(func, n, zeroByteArray);
            }
            this.SharedState.PushValueInScope(bigIntValue, ResolvedType.New(QsResolvedTypeKind.BigInt));
            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnBitwiseAnd(TypedExpression lhs, TypedExpression rhs)
        {
            this.ProcessSubexpression(lhs);
            Value lhsValue = this.SharedState.ValueStack.Pop();
            this.ProcessSubexpression(rhs);
            Value rhsValue = this.SharedState.ValueStack.Pop();
            if (lhs.ResolvedType.Resolution.IsInt)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.And(lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsBigInt)
            {
                var func = this.SharedState.GetRuntimeFunction("bigint_bitand");
                this.SharedState.PushValueInScope(this.SharedState.CurrentBuilder.Call(func, lhsValue, rhsValue),
                    lhs.ResolvedType);
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(lhsValue.NativeType));
            }
            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnBitwiseExclusiveOr(TypedExpression lhs, TypedExpression rhs)
        {
            this.ProcessSubexpression(lhs);
            Value lhsValue = this.SharedState.ValueStack.Pop();
            this.ProcessSubexpression(rhs);
            Value rhsValue = this.SharedState.ValueStack.Pop();
            if (lhs.ResolvedType.Resolution.IsInt)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.Xor(lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsBigInt)
            {
                var func = this.SharedState.GetRuntimeFunction("bigint_bitxor");
                this.SharedState.PushValueInScope(this.SharedState.CurrentBuilder.Call(func, lhsValue, rhsValue),
                    lhs.ResolvedType);
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(lhsValue.NativeType));
            }
            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnBitwiseNot(TypedExpression ex)
        {
            this.ProcessSubexpression(ex);
            Value exValue = this.SharedState.ValueStack.Pop();

            if (ex.ResolvedType.Resolution.IsInt)
            {
                Value minusOne = this.SharedState.CurrentContext.CreateConstant((long)-1);
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.Xor(exValue, minusOne));
            }
            else if (ex.ResolvedType.Resolution.IsBigInt)
            {
                var func = this.SharedState.GetRuntimeFunction("bigint_bitnot");
                this.SharedState.PushValueInScope(this.SharedState.CurrentBuilder.Call(func, exValue),
                    ex.ResolvedType);
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(exValue.NativeType));
            }

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnBitwiseOr(TypedExpression lhs, TypedExpression rhs)
        {
            this.ProcessSubexpression(lhs);
            Value lhsValue = this.SharedState.ValueStack.Pop();
            this.ProcessSubexpression(rhs);
            Value rhsValue = this.SharedState.ValueStack.Pop();

            if (lhs.ResolvedType.Resolution.IsInt)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.Or(lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsBigInt)
            {
                var func = this.SharedState.GetRuntimeFunction("bigint_bitor");
                this.SharedState.PushValueInScope(this.SharedState.CurrentBuilder.Call(func, lhsValue, rhsValue),
                    lhs.ResolvedType);
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(lhsValue.NativeType));
            }

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnBoolLiteral(bool b)
        {
            Value lit = this.SharedState.CurrentContext.CreateConstant(b);
            this.SharedState.ValueStack.Push(lit);
            return ResolvedExpression.InvalidExpr;
        }

        #region Partial applications
        abstract class RebuildItem
        {
            public GenerationContext SharedState { get; set; }
            public ITypeRef ItemType { get; set; }
            public abstract Value BuildItem(InstructionBuilder builder, ITypeRef captureType, Value capture,
                ITypeRef parArgsType, Value parArgs);
        }

        private class InnerCapture : RebuildItem
        {
            public int CaptureIndex { get; set; }

            public override Value BuildItem(InstructionBuilder builder, ITypeRef captureType, Value capture,
                ITypeRef parArgsType, Value parArgs)
            {
                var indices = new Value[] {
                    builder.Context.CreateConstant(0L),
                    builder.Context.CreateConstant(this.CaptureIndex)
                };
                var srcPtr = builder.GetElementPtr(captureType, capture, indices);
                var item = builder.Load(this.ItemType, srcPtr);
                return item;
            }
        }

        private class InnerArg : RebuildItem
        {
            public int ArgIndex { get; set; }

            public override Value BuildItem(InstructionBuilder builder, ITypeRef captureType, Value capture,
                ITypeRef parArgsType, Value parArgs)
            {
                if (this.SharedState.IsTupleType(parArgs.NativeType))
                {
                    var indices = new Value[] {
                        builder.Context.CreateConstant(0L),
                        builder.Context.CreateConstant(this.ArgIndex)
                    };
                    var srcPtr = builder.GetElementPtr(parArgsType, parArgs, indices);
                    var item = builder.Load(this.ItemType, srcPtr);
                    return item;
                }
                else
                {
                    return parArgs;
                }
            }
        }

        private class InnerTuple : RebuildItem
        {
            public ResolvedType TupleType { get; set; }
            public readonly List<RebuildItem> Items = new List<RebuildItem>();

            public override Value BuildItem(InstructionBuilder builder, ITypeRef captureType, Value capture,
                ITypeRef parArgsType, Value parArgs)
            {
                var size = this.SharedState.ComputeSizeForType(this.ItemType, builder);
                var innerTuple = builder.Call(this.SharedState.GetRuntimeFunction("tuple_create"), size);
                this.SharedState.ScopeMgr.AddValue(innerTuple, this.TupleType);
                var typedTuple = builder.BitCast(innerTuple, this.ItemType.CreatePointerType());
                for (int i = 0; i < this.Items.Count; i++)
                {
                    var indices = new Value[] { builder.Context.CreateConstant(0L), builder.Context.CreateConstant(i + 1) };
                    var itemDestPtr = builder.GetElementPtr(this.ItemType, typedTuple, indices);
                    var item = this.Items[i].BuildItem(builder, captureType, capture, parArgsType, parArgs);
                    builder.Store(item, itemDestPtr);
                }
                return innerTuple;
            }
        }

        public override ResolvedExpression OnCallLikeExpression(TypedExpression method, TypedExpression arg)
        {
            static (TypedExpression baseMethod, bool adjoint, int controlled)
                ResolveModifiers(TypedExpression m, bool a, int c) => m.Expression switch
                {
                    ResolvedExpression.AdjointApplication adj => ResolveModifiers(adj.Item, !a, c),
                    ResolvedExpression.ControlledApplication con => ResolveModifiers(con.Item, a, c + 1),
                    _ => (m, a, c),
                };

            static QsSpecializationKind GetSpecializationKind(bool isAdjoint, bool isControlled) => 
                isAdjoint && isControlled ? QsSpecializationKind.QsControlledAdjoint
                    : (isAdjoint ? QsSpecializationKind.QsAdjoint
                    : (isControlled ? QsSpecializationKind.QsControlled
                    : QsSpecializationKind.QsBody));

            void CallQuantumInstruction(string instructionName)
            {
                var func = this.SharedState.GetQuantumFunction(instructionName);
                var argArray = (arg.Expression is ResolvedExpression.ValueTuple tuple)
                    ? tuple.Item.Select(this.EvaluateSubexpression).ToArray()
                    : new Value[] { this.EvaluateSubexpression(arg) };
                var result = this.SharedState.CurrentBuilder.Call(func, argArray);
                this.SharedState.ValueStack.Push(result);
            }

            void InlineCalledRoutine(QsCallable inlinedCallable, bool isAdjoint, bool isControlled)
            {
                var inlineKind = GetSpecializationKind(isAdjoint, isControlled);
                var inlinedSpecialization = inlinedCallable.Specializations.Where(spec => spec.Kind == inlineKind).Single();
                if (isAdjoint && inlinedSpecialization.Implementation.IsGenerated &&
                    (inlinedSpecialization.Implementation as SpecializationImplementation.Generated).Item.IsSelfInverse)
                {
                    inlinedSpecialization = inlinedCallable.Specializations.Where(spec => spec.Kind == QsSpecializationKind.QsBody).Single();
                }

                this.SharedState.StartInlining();
                if (inlinedSpecialization.Implementation is SpecializationImplementation.Provided impl)
                {
                    this.SharedState.MapTuple(arg, impl.Item1);
                    this.Transformation.Statements.OnScope(impl.Item2);
                }
                this.SharedState.StopInlining();

                // If the inlined routine returns Unit, we need to push an extra empty tuple on the stack
                if (inlinedCallable.Signature.ReturnType.Resolution.IsUnitType)
                {
                    this.SharedState.ValueStack.Push(this.SharedState.QirTuplePointer.GetNullValue());
                }
            }

            Value ExtractSingleArg()
            {
                return arg.ResolvedType.Resolution.IsTupleType
                    ? this.EvaluateSubexpression((arg.Expression as ResolvedExpression.ValueTuple).Item[0])
                    : this.EvaluateSubexpression(arg);
            }

            void CallCoreFunction(string name)
            {
                if (name == BuiltIn.Length.FullName.Name.Value)
                {
                    // The argument should be an array
                    var arrayArg = ExtractSingleArg();
                    var lengthFunc = this.SharedState.GetRuntimeFunction("array_get_length");
                    this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.Call(lengthFunc, arrayArg,
                        this.SharedState.CurrentContext.CreateConstant(0)));
                }
                else if (name == "RangeStart")
                {
                    // The argument should be an range
                    var rangeArg = ExtractSingleArg();
                    var start = this.SharedState.CurrentBuilder.ExtractValue(rangeArg, 0u);
                    this.SharedState.ValueStack.Push(start);
                }
                else if (name == "RangeStep")
                {
                    // The argument should be an range
                    var rangeArg = ExtractSingleArg();
                    var step = this.SharedState.CurrentBuilder.ExtractValue(rangeArg, 1u);
                    this.SharedState.ValueStack.Push(step);
                }
                else if (name == "RangeEnd")
                {
                    // The argument should be an range
                    var rangeArg = ExtractSingleArg();
                    var end = this.SharedState.CurrentBuilder.ExtractValue(rangeArg, 2u);
                    this.SharedState.ValueStack.Push(end);
                }
                else if (name == BuiltIn.RangeReverse.FullName.Name.Value)
                {
                    // The argument should be an range
                    var rangeArg = ExtractSingleArg();
                    var start = this.SharedState.CurrentBuilder.ExtractValue(rangeArg, 0u);
                    var step = this.SharedState.CurrentBuilder.ExtractValue(rangeArg, 1u);
                    var end = this.SharedState.CurrentBuilder.ExtractValue(rangeArg, 2u);
                    var newStart = this.SharedState.CurrentBuilder.Add(start,
                        this.SharedState.CurrentBuilder.Mul(step,
                        this.SharedState.CurrentBuilder.SDiv(
                            this.SharedState.CurrentBuilder.Sub(end, start), step)));
                    var newRange = this.SharedState.CurrentBuilder.Load(this.SharedState.QirRange, 
                        this.SharedState.QirEmptyRange);
                    var reversedRange = this.SharedState.CurrentBuilder.InsertValue(newRange, newStart, 0u);
                    reversedRange = this.SharedState.CurrentBuilder.InsertValue(newRange, this.SharedState.CurrentBuilder.Neg(step), 1u);
                    reversedRange = this.SharedState.CurrentBuilder.InsertValue(newRange, start, 2u);
                    this.SharedState.ValueStack.Push(reversedRange);
                }
            }

            Value[] BuildControlledArgList(int controlledCount)
            {
                // The arglist will be a 2-tuple with the first element an array of qubits and the second element
                // a 2-tuple containing an array of qubits and another tuple -- possibly with more nesting levels
                var tuple = arg.Expression as ResolvedExpression.ValueTuple;
                var controlArray = this.EvaluateSubexpression(tuple.Item[0]);
                var arrayType = tuple.Item[0].ResolvedType;
                var remainingArgs = tuple.Item[1];
                ResolvedExpression.ValueTuple innerTuple = null;
                while (--controlledCount > 0)
                {
                    innerTuple = remainingArgs.Expression as ResolvedExpression.ValueTuple;
                    controlArray = this.SharedState.CurrentBuilder.Call(
                        this.SharedState.GetRuntimeFunction("array_concatenate"), controlArray,
                        this.EvaluateSubexpression(innerTuple.Item[0]));
                    this.SharedState.ScopeMgr.AddValue(controlArray, arrayType);
                    if (controlledCount > 1)
                    {
                        remainingArgs = innerTuple.Item[1];
                    }
                }
                var listOfArgs = new List<Value>(new Value[] { controlArray });
                listOfArgs.AddRange(innerTuple?.Item.Skip(1).Select(this.EvaluateSubexpression));
                return listOfArgs.ToArray();
            }

            void CallGlobal(Identifier.GlobalCallable callable, QsResolvedTypeKind methodType, bool isAdjoint,
                int controlledCount)
            {
                var kind = GetSpecializationKind(isAdjoint, controlledCount > 0);
                var func = this.SharedState.GetFunctionByName(callable.Item.Namespace.Value, callable.Item.Name.Value, kind);

                Value[] argList;
                
                // If the operation has more than one "Controlled" functor applied, we will need to adjust the arg list
                // and build a single array of control qubits
                if (controlledCount > 1)
                {
                    argList = BuildControlledArgList(controlledCount);
                }
                else if (arg.ResolvedType.Resolution.IsUnitType)
                {
                    argList = new Value[] { };
                }
                else if (arg.ResolvedType.Resolution.IsTupleType)
                {
                    argList = (arg.Expression as ResolvedExpression.ValueTuple).Item.Select(this.EvaluateSubexpression).ToArray();
                }
                else
                {
                    argList = new Value[] { this.EvaluateSubexpression(arg) };
                }

                var result = this.SharedState.CurrentBuilder.Call(func, argList);
                this.SharedState.ValueStack.Push(result);
                var resultType = methodType.IsOperation
                    ? (methodType as QsResolvedTypeKind.Operation).Item1.Item2
                    : (methodType as QsResolvedTypeKind.Function).Item2;
                this.SharedState.ScopeMgr.AddValue(result, resultType);
            }

            void CallCallableValue()
            {
                // Build the arg tuple
                ResolvedType argType = arg.ResolvedType;
                Value argTuple;
                if (argType.Resolution.IsUnitType)
                {
                    argTuple = this.SharedState.QirTuplePointer.GetNullValue();
                }
                else
                {
                    ITypeRef argStructType = this.SharedState.LlvmStructTypeFromQsharpType(argType);
                    Value argStruct = this.SharedState.CreateTupleForType(argStructType);
                    this.SharedState.FillTuple(argStruct, arg);
                    argTuple = this.SharedState.CurrentBuilder.BitCast(argStruct, this.SharedState.QirTuplePointer);
                }

                // Allocate the result tuple, if needed
                ResolvedType resultResolvedType = this.SharedState.ExpressionTypeStack.Peek();
                ITypeRef resultStructType = null;
                Value resultStruct = null;
                Value resultTuple = null;
                if (resultResolvedType.Resolution.IsUnitType)
                {
                    resultTuple = this.SharedState.QirTuplePointer.GetNullValue();
                }
                else
                {
                    resultStructType = this.SharedState.LlvmStructTypeFromQsharpType(resultResolvedType);
                    resultTuple = this.SharedState.CreateTupleForType(resultStructType);
                    resultStruct = this.SharedState.CurrentBuilder.BitCast(resultTuple, resultStructType.CreatePointerType());
                }

                var callableValue = this.EvaluateSubexpression(method);
                var func = this.SharedState.GetRuntimeFunction("callable_invoke");
                this.SharedState.CurrentBuilder.Call(func, callableValue, argTuple, resultTuple);

                // Now push the result. For now we assume it's a scalar.
                if (resultResolvedType.Resolution.IsUnitType)
                {
                    this.SharedState.ValueStack.Push(this.SharedState.QirTuplePointer.GetNullValue());
                }
                else
                {
                    var indices = new Value[] { this.SharedState.CurrentContext.CreateConstant(0L),
                                                            this.SharedState.CurrentContext.CreateConstant(1) };
                    Value resultPointer = this.SharedState.CurrentBuilder.GetElementPtr(resultStructType, resultStruct, indices);
                    ITypeRef resultType = this.SharedState.LlvmTypeFromQsharpType(resultResolvedType);
                    Value result = this.SharedState.CurrentBuilder.Load(resultType, resultPointer);
                    this.SharedState.ValueStack.Push(result);
                }
            }

            if (TypedExpression.IsPartialApplication(ResolvedExpression.NewCallLikeExpression(method, arg)))
            {
                BuildPartialApplication(method, arg);
            }
            else if (this.SharedState.IsQuantumInstructionCall(method, out string instructionName))
            {
                // Handle the special case of a call to an operation that maps directly to a quantum instruction.
                // Note that such an operation will never have an Adjoint or Controlled specialization.
                CallQuantumInstruction(instructionName);
            }
            else
            {
                // Resolve Adjoint and Controlled modifiers
                var (baseMethod, isAdjoint, controlledCount) = ResolveModifiers(method, false, 0);

                // Check for, and handle, inlining
                if (this.SharedState.IsInlined(baseMethod, out QsCallable inlinedCallable))
                {
                    InlineCalledRoutine(inlinedCallable, isAdjoint, controlledCount > 0);
                }
                else if ((baseMethod.Expression is ResolvedExpression.Identifier id) && id.Item1.IsGlobalCallable)
                {
                    // A direct call to a top-level callable
                    Identifier.GlobalCallable callable = id.Item1 as Identifier.GlobalCallable;

                    // Is it a call to a built-in?
                    if (callable.Item.Namespace.Value == BuiltIn.CoreNamespace.Value)
                    {
                        CallCoreFunction(callable.Item.Name.Value);
                    }
                    else
                    {
                        CallGlobal(callable, baseMethod.ResolvedType.Resolution, isAdjoint, controlledCount);
                    }
                }
                else
                {
                    CallCallableValue();
                }
            }

            return ResolvedExpression.InvalidExpr;
        }

        private void BuildPartialApplication(TypedExpression method, TypedExpression arg)
        {
            RebuildItem BuildPartialArgList(ResolvedType argType, TypedExpression arg, List<ResolvedType> remainingArgs,
                List<(Value, ResolvedType)> capturedValues)
            {
                // We need argType because _'s -- missing expressions -- have MissingType, rather than the actual type.
                if (arg.Expression.IsMissingExpr)
                {
                    var rebuild = new InnerArg()
                    {
                        SharedState = this.SharedState,
                        ArgIndex = remainingArgs.Count + 1,
                        ItemType = this.SharedState.LlvmTypeFromQsharpType(argType)
                    };
                    remainingArgs.Add(argType);
                    return rebuild;
                }
                else if (arg.Expression is ResolvedExpression.ValueTuple tuple)
                {
                    var types = argType.Resolution as QsResolvedTypeKind.TupleType;
                    var rebuild = new InnerTuple() 
                    {
                        SharedState = this.SharedState,
                        TupleType = argType,
                        ItemType = this.SharedState.CurrentContext.CreateStructType(false, this.SharedState.QirTupleHeader,
                                types.Item.Select(i => this.SharedState.LlvmTypeFromQsharpType(i)).ToArray())
                    };
                    for (var i = 0; i < tuple.Item.Length; i++)
                    {
                        var itemRebuild = BuildPartialArgList(types.Item[i], tuple.Item[i], remainingArgs, capturedValues);
                        rebuild.Items.Add(itemRebuild);
                    }
                    return rebuild;
                }
                else
                {
                    // A value we should capture; remember that the first element in the capture tuple is the inner
                    // callable
                    var rebuild = new InnerCapture()
                    {
                        SharedState = this.SharedState,
                        CaptureIndex = capturedValues.Count + 2,
                        ItemType = this.SharedState.LlvmTypeFromQsharpType(arg.ResolvedType)
                    };
                    var val = this.EvaluateSubexpression(arg);
                    capturedValues.Add((val, argType));
                    return rebuild;
                }
            }

            Value GetSpecializedInnerCallable(Value innerCallable, QsSpecializationKind kind, InstructionBuilder builder)
            {
                if (kind == QsSpecializationKind.QsBody)
                {
                    return innerCallable;
                }
                else
                {
                    var copier = this.SharedState.GetRuntimeFunction("callable_copy");
                    var copy = builder.Call(copier, innerCallable);
                    this.SharedState.ScopeMgr.AddValue(copy,
                        ResolvedType.New(QsResolvedTypeKind.NewOperation(
                            Tuple.Create(ResolvedType.New(QsResolvedTypeKind.UnitType),
                                ResolvedType.New(QsResolvedTypeKind.UnitType)),
                            CallableInformation.NoInformation)));
                    if (kind == QsSpecializationKind.QsAdjoint)
                    {
                        var adj = this.SharedState.GetRuntimeFunction("callable_make_adjoint");
                        builder.Call(adj, copy);
                    }
                    else if (kind == QsSpecializationKind.QsControlled)
                    {
                        var ctl = this.SharedState.GetRuntimeFunction("callable_make_controlled");
                        builder.Call(ctl, copy);
                    }
                    else // Ctl+Adj
                    {
                        var adj = this.SharedState.GetRuntimeFunction("callable_make_adjoint");
                        var ctl = this.SharedState.GetRuntimeFunction("callable_make_controlled");
                        builder.Call(adj, copy);
                        builder.Call(ctl, copy);
                    }
                    return copy;
                }
            }

            IrFunction BuildLiftedSpecialization(string name, QsSpecializationKind kind, ITypeRef captureType, ITypeRef parArgsType, RebuildItem rebuild)
            {
                var funcName = GenerationContext.CallableWrapperName("Lifted", name, kind);
                var func = this.SharedState.CurrentModule.AddFunction(funcName, this.SharedState.StandardWrapperSignature);

                func.Parameters[0].Name = "capture-tuple";
                func.Parameters[1].Name = "arg-tuple";
                func.Parameters[2].Name = "result-tuple";
                var entry = func.AppendBasicBlock("entry");
                var builder = new InstructionBuilder(entry);
                this.SharedState.ScopeMgr.OpenScope();

                var capturePointer = builder.BitCast(func.Parameters[0], captureType.CreatePointerType());
                Value innerArgTuple;
                if ((kind == QsSpecializationKind.QsControlled) || (kind == QsSpecializationKind.QsControlledAdjoint))
                {
                    // Deal with the extra control qubit arg for controlled and controlled-adjoint
                    // Note that there's a special case if the base specialization only takes a single parameter,
                    // in which case we don't create the sub-tuple.
                    if ((parArgsType as IStructType).Members.Count > 2)
                    {
                        var ctlArgsType = this.SharedState.CurrentContext.CreateStructType(false,
                            this.SharedState.QirTupleHeader, this.SharedState.QirArray, this.SharedState.QirTuplePointer);
                        var ctlArgsPointer = builder.BitCast(func.Parameters[1], ctlArgsType.CreatePointerType());
                        var controlsPointer = builder.GetElementPtr(ctlArgsType, ctlArgsPointer,
                            new Value[] { this.SharedState.CurrentContext.CreateConstant(0L), this.SharedState.CurrentContext.CreateConstant(1) });
                        var restPointer = builder.GetElementPtr(ctlArgsType, ctlArgsPointer,
                            new Value[] { this.SharedState.CurrentContext.CreateConstant(0L), this.SharedState.CurrentContext.CreateConstant(2) });
                        var typedRestPointer = builder.BitCast(restPointer, parArgsType.CreatePointerType());
                        var restTuple = rebuild.BuildItem(builder, captureType, capturePointer, parArgsType, typedRestPointer);
                        var size = this.SharedState.ComputeSizeForType(ctlArgsType, builder);
                        innerArgTuple = builder.Call(this.SharedState.GetRuntimeFunction("tuple_create"), size);
                        this.SharedState.ScopeMgr.AddValue(innerArgTuple);
                        var typedNewTuple = builder.BitCast(innerArgTuple, ctlArgsType.CreatePointerType());
                        var destControlsPointer = builder.GetElementPtr(ctlArgsType, typedNewTuple,
                            new Value[] { this.SharedState.CurrentContext.CreateConstant(0L), this.SharedState.CurrentContext.CreateConstant(1) });
                        var controls = builder.Load(this.SharedState.QirArray, controlsPointer);
                        builder.Store(controls, destControlsPointer);
                        var destArgsPointer = builder.GetElementPtr(ctlArgsType, typedNewTuple,
                            new Value[] { this.SharedState.CurrentContext.CreateConstant(0L), this.SharedState.CurrentContext.CreateConstant(2) });
                        builder.Store(restTuple, destArgsPointer);
                    }
                    else
                    {
                        // First process the incoming argument. Remember, [0] is the %TupleHeader.
                        var singleArgType = (parArgsType as IStructType).Members[1];
                        var inputArgsType = this.SharedState.CurrentContext.CreateStructType(false,
                            this.SharedState.QirTupleHeader, this.SharedState.QirArray, singleArgType);
                        var inputArgsPointer = builder.BitCast(func.Parameters[1], inputArgsType.CreatePointerType());
                        var controlsPointer = builder.GetElementPtr(inputArgsType, inputArgsPointer,
                            new Value[] { this.SharedState.CurrentContext.CreateConstant(0L), this.SharedState.CurrentContext.CreateConstant(1) });
                        var restPointer = builder.GetElementPtr(inputArgsType, inputArgsPointer,
                            new Value[] { this.SharedState.CurrentContext.CreateConstant(0L), this.SharedState.CurrentContext.CreateConstant(2) });
                        var restValue = builder.Load(singleArgType, restPointer);

                        // OK, now build the full args for the partially-applied callable, other than the controlled qubits
                        var restTuple = rebuild.BuildItem(builder, captureType, capturePointer, singleArgType, restValue);
                        // The full args for the inner callable will include the controls
                        var innerArgType = this.SharedState.CurrentContext.CreateStructType(false,
                            this.SharedState.QirTupleHeader, this.SharedState.QirArray, restTuple.NativeType);
                        var size = this.SharedState.ComputeSizeForType(innerArgType, builder);
                        innerArgTuple = builder.Call(this.SharedState.GetRuntimeFunction("tuple_create"), size);
                        this.SharedState.ScopeMgr.AddValue(innerArgTuple);
                        var typedNewTuple = builder.BitCast(innerArgTuple, innerArgType.CreatePointerType());
                        var destControlsPointer = builder.GetElementPtr(innerArgType, typedNewTuple,
                            new Value[] { this.SharedState.CurrentContext.CreateConstant(0L), this.SharedState.CurrentContext.CreateConstant(1) });
                        var controls = builder.Load(this.SharedState.QirArray, controlsPointer);
                        builder.Store(controls, destControlsPointer);
                        var destArgsPointer = builder.GetElementPtr(innerArgType, typedNewTuple,
                            new Value[] { this.SharedState.CurrentContext.CreateConstant(0L), this.SharedState.CurrentContext.CreateConstant(2) });
                        builder.Store(restTuple, destArgsPointer);
                    }
                }
                else
                {
                    var parArgsPointer = builder.BitCast(func.Parameters[1], parArgsType.CreatePointerType());
                    innerArgTuple = rebuild.BuildItem(builder, captureType, capturePointer, parArgsType, parArgsPointer);
                }

                var innerCallablePtr = builder.GetElementPtr(captureType, capturePointer,
                        new Value[] { this.SharedState.CurrentContext.CreateConstant(0L), this.SharedState.CurrentContext.CreateConstant(1) });
                var innerCallable = builder.Load(this.SharedState.QirCallable, innerCallablePtr);
                // Depending on the specialization, we may have to get a different specialization of the callable
                var specToCall = GetSpecializedInnerCallable(innerCallable, kind, builder);
                var invoke = this.SharedState.GetRuntimeFunction("callable_invoke");
                builder.Call(invoke, specToCall, innerArgTuple, func.Parameters[2]);

                this.SharedState.ScopeMgr.ForceCloseScope(builder);

                builder.Return();

                return func;
            }

            // Figure out the inputs to the resulting callable based on the signature of the partial application expression.
            var paType = this.SharedState.ExpressionTypeStack.Peek();
            var paArgTuple = paType.Resolution switch
            {
                QsResolvedTypeKind.Function paf => paf.Item1,
                QsResolvedTypeKind.Operation pao => pao.Item1.Item1,
                _ => throw new InvalidOperationException("Partial application of a non-callable value")
            };
            var partialArgType = paArgTuple.Resolution switch
            {
                QsResolvedTypeKind.TupleType pat => this.SharedState.CurrentContext.CreateStructType(false,
                    this.SharedState.QirTupleHeader, pat.Item.Select(this.SharedState.LlvmTypeFromQsharpType).ToArray()),
                _ => this.SharedState.LlvmStructTypeFromQsharpType(paArgTuple)
            };

            // And the inputs to the underlying callable
            var innerTupleType = method.ResolvedType.Resolution switch
            {
                QsResolvedTypeKind.Function paf => paf.Item1,
                QsResolvedTypeKind.Operation pao => pao.Item1.Item1,
                _ => throw new InvalidOperationException("Partial application of a non-callable value")
            };

            // Figure out the args & signature of the resulting callable
            var parArgs = new List<ResolvedType>();
            var caps = new List<(Value, ResolvedType)>();
            var rebuild = BuildPartialArgList(innerTupleType, arg, parArgs, caps);

            // Create the capture tuple
            // Note that we set aside the first element of the capture tuple for the inner operation to call
            var capTypeList = (new ITypeRef[] { this.SharedState.QirCallable })
                .Concat(caps.Select(c => c.Item1.NativeType)).ToArray();
            var capType = this.SharedState.CurrentContext.CreateStructType(false, this.SharedState.QirTupleHeader,
                capTypeList);
            var cap = this.SharedState.CreateTupleForType(capType);
            var capture = this.SharedState.CurrentBuilder.BitCast(cap, capType.CreatePointerType());
            var callablePointer = this.SharedState.CurrentBuilder.GetElementPtr(capType, capture,
                new Value[] { this.SharedState.CurrentContext.CreateConstant(0L), this.SharedState.CurrentContext.CreateConstant(1) });
            var innerCallable = this.EvaluateSubexpression(method);
            this.SharedState.CurrentBuilder.Store(innerCallable, callablePointer);
            this.SharedState.ScopeMgr.RemovePendingValue(innerCallable);
            //AddRef(method.ResolvedType, innerCallable);
            for (int n = 0; n < caps.Count; n++)
            {
                var item = this.SharedState.CurrentBuilder.GetElementPtr(capType, capture,
                    new Value[] { this.SharedState.CurrentContext.CreateConstant(0L), this.SharedState.CurrentContext.CreateConstant(n+2) });
                this.SharedState.CurrentBuilder.Store(caps[n].Item1, item);
                this.SharedState.AddRef(caps[n].Item1);
            }

            // Create the lifted specialization implementation(s)
            // First, figure out which ones we need to create
            var kinds = new HashSet<QsSpecializationKind>
            {
                QsSpecializationKind.QsBody
            };
            if (method.ResolvedType.Resolution is QsResolvedTypeKind.Operation op)
            {
                if (op.Item2.Characteristics.SupportedFunctors.IsValue)
                {
                    var functors = op.Item2.Characteristics.SupportedFunctors.Item;
                    if (functors.Contains(QsFunctor.Adjoint))
                    {
                        kinds.Add(QsSpecializationKind.QsAdjoint);
                    }
                    if (functors.Contains(QsFunctor.Controlled))
                    {
                        kinds.Add(QsSpecializationKind.QsControlled);
                        if (functors.Contains(QsFunctor.Adjoint))
                        {
                            kinds.Add(QsSpecializationKind.QsControlledAdjoint);
                        }
                    }
                }
            }

            // Now create our specializations
            var liftedName = this.SharedState.GenerateUniqueName("PartialApplication");
            var specializations = new Constant[4];
            for (var index = 0; index < 4; index++)
            {
                var kind = this.SharedState.FunctionArray[index];
                if (kinds.Contains(kind))
                {
                    specializations[index] = BuildLiftedSpecialization(liftedName, kind, capType,
                        partialArgType, rebuild);
                }
                else
                {
                    specializations[index] = Constant.NullValueFor(specializations[0].NativeType);
                }
            }

            // Build the array
            var t = specializations[0].NativeType;
            var array = ConstantArray.From(t, specializations);
            var table = this.SharedState.CurrentModule.AddGlobal(array.NativeType, true, Linkage.DllExport, array,
                liftedName);

            // Create the callable
            var func = this.SharedState.GetRuntimeFunction("callable_create");
            var callableValue = this.SharedState.CurrentBuilder.Call(func, table, cap);

            this.SharedState.ValueStack.Push(callableValue);
            // We cheat on the type because all that the scope manager cares about is that it's a callable
            this.SharedState.ScopeMgr.AddValue(callableValue, method.ResolvedType);
        }
        #endregion

        public override ResolvedExpression OnConditionalExpression(TypedExpression cond, TypedExpression ifTrue, 
            TypedExpression ifFalse)
        {
            static bool ExpressionIsSelfEvaluating(TypedExpression ex)
            {
                return ex.Expression.IsIdentifier || ex.Expression.IsBoolLiteral || ex.Expression.IsDoubleLiteral
                    || ex.Expression.IsIntLiteral || ex.Expression.IsPauliLiteral || ex.Expression.IsRangeLiteral
                    || ex.Expression.IsResultLiteral || ex.Expression.IsUnitValue;
            }

            var condValue = this.EvaluateSubexpression(cond);

            // Special case: if both values are self-evaluating (literals or simple identifiers), we can
            // do this with a select.
            if (ExpressionIsSelfEvaluating(ifTrue) && ExpressionIsSelfEvaluating(ifFalse))
            {
                var trueValue = this.EvaluateSubexpression(ifTrue);
                var falseValue = this.EvaluateSubexpression(ifFalse);
                var select = this.SharedState.CurrentBuilder.Select(condValue, trueValue, falseValue);
                this.SharedState.ValueStack.Push(select);
            }
            else
            {
                // This is similar to conditional statements, but actually a bit simpler because there's always an else,
                // and we don't need to open a new scope. On the other hand, we do need to build a phi node in the
                // continuation block.
                var contBlock = this.SharedState.AddBlockAfterCurrent("condContinue");
                var falseBlock = this.SharedState.AddBlockAfterCurrent("condFalse");
                var trueBlock = this.SharedState.AddBlockAfterCurrent("condTrue");

                this.SharedState.CurrentBuilder.Branch(condValue, trueBlock, falseBlock);

                this.SharedState.SetCurrentBlock(trueBlock);
                var trueValue = this.EvaluateSubexpression(ifTrue);
                this.SharedState.CurrentBuilder.Branch(contBlock);

                this.SharedState.SetCurrentBlock(falseBlock);
                var falseValue = this.EvaluateSubexpression(ifFalse);
                this.SharedState.CurrentBuilder.Branch(contBlock);

                this.SharedState.SetCurrentBlock(contBlock);
                var phi = this.SharedState.CurrentBuilder.PhiNode(trueValue.NativeType);
                phi.AddIncoming(trueValue, trueBlock);
                phi.AddIncoming(falseValue, falseBlock);

                this.SharedState.ValueStack.Push(phi);
            }

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnControlledApplication(TypedExpression ex)
        {
            // ex will evaluate to a callable
            var baseCallable = this.EvaluateSubexpression(ex);

            // If ex was a variable, we need to make a copy before we take the adjoint.
            Value callable;

            if ((ex.Expression is ResolvedExpression.Identifier id) && (id.Item1.IsLocalVariable))
            {
                var copier = this.SharedState.GetRuntimeFunction("callable_copy");
                callable = this.SharedState.CurrentBuilder.Call(copier, baseCallable);
                this.SharedState.ScopeMgr.AddValue(callable, ex.ResolvedType);
            }
            else
            {
                callable = baseCallable;
            }

            var adjointer = this.SharedState.GetRuntimeFunction("callable_make_controlled");
            this.SharedState.CurrentBuilder.Call(adjointer, callable);
            this.SharedState.ValueStack.Push(callable);

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnCopyAndUpdateExpression(TypedExpression lhs, TypedExpression accEx, TypedExpression rhs)
        {
            if (lhs.ResolvedType.Resolution.IsArrayType)
            {
                var array = this.SharedState.GetWritableCopy(lhs);
                if (accEx.ResolvedType.Resolution.IsInt)
                {
                    var index = this.EvaluateSubexpression(accEx);
                    var value = this.EvaluateSubexpression(rhs);
                    var elementType = this.SharedState.LlvmTypeFromQsharpType(
                        (lhs.ResolvedType.Resolution as QsResolvedTypeKind.ArrayType).Item);
                    var rawElementPtr = this.SharedState.CurrentBuilder.Call(
                        this.SharedState.GetRuntimeFunction("array_get_element_ptr_1d"), array, index);
                    var elementPtr = this.SharedState.CurrentBuilder.BitCast(rawElementPtr, elementType.CreatePointerType());
                    this.SharedState.CurrentBuilder.Store(value, elementPtr);
                }
                else if (accEx.ResolvedType.Resolution.IsRange)
                {
                    // TODO: handle range updates
                    throw new NotImplementedException("Array slice updates");
                }
                this.SharedState.ValueStack.Push(array);
            }
            else if (lhs.ResolvedType.Resolution is QsResolvedTypeKind.UserDefinedType tt)
            {
                var location = new List<(int, ITypeRef)>();
                if (this.SharedState.TryFindUDT(tt.Item.Namespace.Value, tt.Item.Name.Value, out QsCustomType udt)
                    && (accEx.Expression is ResolvedExpression.Identifier acc)
                    && (acc.Item1 is Identifier.LocalVariable loc)
                    && this.FindNamedItem(loc.Item.Value, udt.TypeItems, location))
                {
                    // The location list is backwards, by design, so we have to reverse it
                    location.Reverse();
                    var copy = this.SharedState.GetWritableCopy(lhs);
                    var current = copy;
                    for (int i = 0; i < location.Count; i++)
                    {
                        var indices = new Value[] { this.SharedState.CurrentContext.CreateConstant(0L),
                                                    this.SharedState.CurrentContext.CreateConstant(location[i].Item1) };
                        var ptr = this.SharedState.CurrentBuilder.GetElementPtr((location[i].Item2 as IPointerType).ElementType,
                            current, indices);
                        // For the last item on the list, we store; otherwise, we load the next tuple
                        if (i == location.Count - 1)
                        {
                            var value = this.EvaluateSubexpression(rhs);
                            this.SharedState.CurrentBuilder.Store(value, ptr);
                        }
                        else
                        {
                            current = this.SharedState.CurrentBuilder.Load(location[i+1].Item2, ptr);
                        }
                    }
                    this.SharedState.ValueStack.Push(copy);
                }
                else
                {
                    this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(this.SharedState.QirTuplePointer));
                }
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(this.SharedState.QirInt));
            }
            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnDivision(TypedExpression lhs, TypedExpression rhs)
        {
            this.ProcessSubexpression(lhs);
            Value lhsValue = this.SharedState.ValueStack.Pop();
            this.ProcessSubexpression(rhs);
            Value rhsValue = this.SharedState.ValueStack.Pop();
            if (lhs.ResolvedType.Resolution.IsInt)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.SDiv(lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsDouble)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.FDiv(lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsBigInt)
            {
                var func = this.SharedState.GetRuntimeFunction("bigint_divide");
                this.SharedState.PushValueInScope(this.SharedState.CurrentBuilder.Call(func, lhsValue, rhsValue),
                    lhs.ResolvedType);
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(lhsValue.NativeType));
            }
            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnDoubleLiteral(double d)
        {
            Value lit = this.SharedState.CurrentContext.CreateConstant(d);
            this.SharedState.ValueStack.Push(lit);
            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnEquality(TypedExpression lhs, TypedExpression rhs)
        {
            // Get the Value for the lhs and rhs
            this.ProcessSubexpression(lhs);
            Value lhsValue = this.SharedState.ValueStack.Pop();
            this.ProcessSubexpression(rhs);
            Value rhsValue = this.SharedState.ValueStack.Pop();

            // The code we generate here is highly dependent on the type of the expression
            if (lhs.ResolvedType.Resolution.IsResult)
            {
                // Generate a call to the result equality testing function
                this.SharedState.ValueStack.Push(
                    this.SharedState.CurrentBuilder.Call(this.SharedState.GetRuntimeFunction("result_equal"),
                    lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsBool || lhs.ResolvedType.Resolution.IsInt || lhs.ResolvedType.Resolution.IsQubit
                || lhs.ResolvedType.Resolution.IsPauli)
            {
                // Works for pointers as well as integer types
                this.SharedState.ValueStack.Push(
                    this.SharedState.CurrentBuilder.Compare(Llvm.NET.Instructions.IntPredicate.Equal,
                    lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsDouble)
            {
                this.SharedState.ValueStack.Push(
                    this.SharedState.CurrentBuilder.Compare(Llvm.NET.Instructions.RealPredicate.OrderedAndEqual,
                    lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsString)
            {
                // Generate a call to the string equality testing function
                this.SharedState.ValueStack.Push(
                    this.SharedState.CurrentBuilder.Call(this.SharedState.GetRuntimeFunction("string_equal"),
                    lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsBigInt)
            {
                // Generate a call to the bigint equality testing function
                this.SharedState.ValueStack.Push(
                    this.SharedState.CurrentBuilder.Call(this.SharedState.GetRuntimeFunction("bigint_equal"),
                    lhsValue, rhsValue));
            }
            else
            {
                // TODO: Equality testing for general types
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(this.SharedState.CurrentContext.BoolType));
            }

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnExponentiate(TypedExpression lhs, TypedExpression rhs)
        {
            // Get the Value for the lhs and rhs
            this.ProcessSubexpression(lhs);
            Value lhsValue = this.SharedState.ValueStack.Pop();
            this.ProcessSubexpression(rhs);
            Value rhsValue = this.SharedState.ValueStack.Pop();

            if (lhs.ResolvedType.Resolution.IsInt)
            {
                var powFunc = this.SharedState.GetRuntimeFunction("int_power");
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.Call(powFunc, lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsDouble)
            {
                var powFunc = this.SharedState.CurrentModule.GetIntrinsicDeclaration("llvm.pow.f", this.SharedState.QirDouble);
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.Call(powFunc, lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsBigInt)
            {
                // RHS must be an integer that can fit into an i32
                var exponent = this.SharedState.CurrentBuilder.IntCast(rhsValue, this.SharedState.CurrentContext.Int32Type, true);
                var powFunc = this.SharedState.GetRuntimeFunction("bigint_power");
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.Call(powFunc, lhsValue, exponent));
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(this.SharedState.QirInt));
            }

            return ResolvedExpression.InvalidExpr;
        }

        //public override ResolvedExpression OnExpressionKind(ResolvedExpression kind)
        //{
        //    return base.OnExpressionKind(kind);
        //}

        //public override ResolvedExpression OnFunctionCall(TypedExpression method, TypedExpression arg)
        //{
        //    return base.OnFunctionCall(method, arg);
        //}

        public override ResolvedExpression OnGreaterThan(TypedExpression lhs, TypedExpression rhs)
        {
            // Get the Value for the lhs and rhs
            this.ProcessSubexpression(lhs);
            Value lhsValue = this.SharedState.ValueStack.Pop();
            this.ProcessSubexpression(rhs);
            Value rhsValue = this.SharedState.ValueStack.Pop();

            if (lhs.ResolvedType.Resolution.IsInt)
            {
                this.SharedState.ValueStack.Push(
                    this.SharedState.CurrentBuilder.Compare(Llvm.NET.Instructions.IntPredicate.SignedGreater,
                    lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsDouble)
            {
                this.SharedState.ValueStack.Push(
                    this.SharedState.CurrentBuilder.Compare(Llvm.NET.Instructions.RealPredicate.OrderedAndGreaterThan,
                    lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsBigInt)
            {
                var func = this.SharedState.GetRuntimeFunction("bigint_greater");
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.Call(func, lhsValue, rhsValue));
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(this.SharedState.CurrentContext.BoolType));
            }

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnGreaterThanOrEqual(TypedExpression lhs, TypedExpression rhs)
        {
            // Get the Value for the lhs and rhs
            this.ProcessSubexpression(lhs);
            Value lhsValue = this.SharedState.ValueStack.Pop();
            this.ProcessSubexpression(rhs);
            Value rhsValue = this.SharedState.ValueStack.Pop();

            if (lhs.ResolvedType.Resolution.IsInt)
            {
                this.SharedState.ValueStack.Push(
                    this.SharedState.CurrentBuilder.Compare(Llvm.NET.Instructions.IntPredicate.SignedGreaterOrEqual,
                    lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsDouble)
            {
                this.SharedState.ValueStack.Push(
                    this.SharedState.CurrentBuilder.Compare(Llvm.NET.Instructions.RealPredicate.OrderedAndGreaterThanOrEqual,
                    lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsBigInt)
            {
                var func = this.SharedState.GetRuntimeFunction("bigint_greater_eq");
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.Call(func, lhsValue, rhsValue));
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(this.SharedState.CurrentContext.BoolType));
            }

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnIdentifier(Identifier sym, QsNullable<ImmutableArray<ResolvedType>> tArgs)
        {
            if (sym is Identifier.LocalVariable local)
            {
                string name = local.Item.Value;
                this.SharedState.PushNamedValue(name);
            }
            else if (sym is Identifier.GlobalCallable globalCallable)
            {
                if (this.SharedState.TryFindGlobalCallable(globalCallable.Item.Namespace.Value,
                    globalCallable.Item.Name.Value, out QsCallable callable))
                {
                    var wrapper = this.SharedState.EnsureWrapperFor(callable);
                    var func = this.SharedState.GetRuntimeFunction("callable_create");
                    var callableValue = this.SharedState.CurrentBuilder.Call(func, wrapper,
                        this.SharedState.QirTuplePointer.GetNullValue());

                    this.SharedState.ValueStack.Push(callableValue);
                    this.SharedState.ScopeMgr.AddValue(callableValue,
                        ResolvedType.New(QsResolvedTypeKind.NewOperation(
                            new Tuple<ResolvedType, ResolvedType>(callable.Signature.ArgumentType, 
                                                                    callable.Signature.ReturnType),
                            CallableInformation.NoInformation)));
                }
                else
                {
                    this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(this.SharedState.QirCallable));
                }
            }
            else
            {
                // Invalid identifier
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(
                    this.SharedState.LlvmTypeFromQsharpType(this.SharedState.ExpressionTypeStack.Peek())));
            }

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnInequality(TypedExpression lhs, TypedExpression rhs)
        {
            // Get the Value for the lhs and rhs
            this.ProcessSubexpression(lhs);
            Value lhsValue = this.SharedState.ValueStack.Pop();
            this.ProcessSubexpression(rhs);
            Value rhsValue = this.SharedState.ValueStack.Pop();

            // The code we generate here is highly dependent on the type of the expression
            if (lhs.ResolvedType.Resolution.IsResult)
            {
                // Generate a call to the result equality testing function
                var eq = this.SharedState.CurrentBuilder.Call(this.SharedState.GetRuntimeFunction("result_equal"),
                    lhsValue, rhsValue);
                var ineq = this.SharedState.CurrentBuilder.Not(eq);
                this.SharedState.ValueStack.Push(ineq);
            }
            else if (lhs.ResolvedType.Resolution.IsBool || lhs.ResolvedType.Resolution.IsInt || lhs.ResolvedType.Resolution.IsQubit
                || lhs.ResolvedType.Resolution.IsPauli)
            {
                // Works for pointers as well as integer types
                var eq = this.SharedState.CurrentBuilder.Compare(Llvm.NET.Instructions.IntPredicate.Equal,
                    lhsValue, rhsValue);
                var ineq = this.SharedState.CurrentBuilder.Not(eq);
                this.SharedState.ValueStack.Push(ineq);
            }
            else if (lhs.ResolvedType.Resolution.IsDouble)
            {
                var eq = this.SharedState.CurrentBuilder.Compare(Llvm.NET.Instructions.RealPredicate.OrderedAndEqual,
                    lhsValue, rhsValue);
                var ineq = this.SharedState.CurrentBuilder.Not(eq);
                this.SharedState.ValueStack.Push(ineq);
            }
            else if (lhs.ResolvedType.Resolution.IsString)
            {
                // Generate a call to the string equality testing function
                var eq = this.SharedState.CurrentBuilder.Call(this.SharedState.GetRuntimeFunction("string_equal"),
                    lhsValue, rhsValue);
                var ineq = this.SharedState.CurrentBuilder.Not(eq);
                this.SharedState.ValueStack.Push(ineq);
            }
            else if (lhs.ResolvedType.Resolution.IsBigInt)
            {
                // Generate a call to the bigint equality testing function
                var eq = this.SharedState.CurrentBuilder.Call(this.SharedState.GetRuntimeFunction("bigint_equal"),
                    lhsValue, rhsValue);
                var ineq = this.SharedState.CurrentBuilder.Not(eq);
                this.SharedState.ValueStack.Push(ineq);
            }
            else
            {
                // TODO: Equality testing for general types
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(this.SharedState.CurrentContext.BoolType));
            }

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnIntLiteral(long i)
        {
            Value lit = this.SharedState.CurrentContext.CreateConstant(i);
            this.SharedState.ValueStack.Push(lit);
            return ResolvedExpression.InvalidExpr;
        }

        //public override ResolvedExpression OnInvalidExpression()
        //{
        //    return base.OnInvalidExpression();
        //}

        public override ResolvedExpression OnLeftShift(TypedExpression lhs, TypedExpression rhs)
        {
            this.ProcessSubexpression(lhs);
            Value lhsValue = this.SharedState.ValueStack.Pop();
            this.ProcessSubexpression(rhs);
            Value rhsValue = this.SharedState.ValueStack.Pop();
            if (lhs.ResolvedType.Resolution.IsInt)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.ShiftLeft(lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsBigInt)
            {
                var func = this.SharedState.GetRuntimeFunction("bigint_shiftleft");
                this.SharedState.PushValueInScope(this.SharedState.CurrentBuilder.Call(func, lhsValue, rhsValue),
                    lhs.ResolvedType);
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(lhsValue.NativeType));
            }
            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnLessThan(TypedExpression lhs, TypedExpression rhs)
        {
            // Get the Value for the lhs and rhs
            this.ProcessSubexpression(lhs);
            Value lhsValue = this.SharedState.ValueStack.Pop();
            this.ProcessSubexpression(rhs);
            Value rhsValue = this.SharedState.ValueStack.Pop();

            if (lhs.ResolvedType.Resolution.IsInt)
            {
                this.SharedState.ValueStack.Push(
                    this.SharedState.CurrentBuilder.Compare(Llvm.NET.Instructions.IntPredicate.SignedLess,
                    lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsDouble)
            {
                this.SharedState.ValueStack.Push(
                    this.SharedState.CurrentBuilder.Compare(Llvm.NET.Instructions.RealPredicate.OrderedAndLessThan,
                    lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsBigInt)
            {
                var func = this.SharedState.GetRuntimeFunction("bigint_greater_eq");
                this.SharedState.ValueStack.Push(
                    this.SharedState.CurrentBuilder.Not(this.SharedState.CurrentBuilder.Call(func, lhsValue, rhsValue)));
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(this.SharedState.CurrentContext.BoolType));
            }

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnLessThanOrEqual(TypedExpression lhs, TypedExpression rhs)
        {
            // Get the Value for the lhs and rhs
            this.ProcessSubexpression(lhs);
            Value lhsValue = this.SharedState.ValueStack.Pop();
            this.ProcessSubexpression(rhs);
            Value rhsValue = this.SharedState.ValueStack.Pop();

            if (lhs.ResolvedType.Resolution.IsInt)
            {
                this.SharedState.ValueStack.Push(
                    this.SharedState.CurrentBuilder.Compare(Llvm.NET.Instructions.IntPredicate.SignedLessOrEqual,
                    lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsDouble)
            {
                this.SharedState.ValueStack.Push(
                    this.SharedState.CurrentBuilder.Compare(Llvm.NET.Instructions.RealPredicate.OrderedAndLessThanOrEqual,
                    lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsBigInt)
            {
                var func = this.SharedState.GetRuntimeFunction("bigint_greater");
                this.SharedState.ValueStack.Push(
                    this.SharedState.CurrentBuilder.Not(this.SharedState.CurrentBuilder.Call(func, lhsValue, rhsValue)));
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(this.SharedState.CurrentContext.BoolType));
            }

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnLogicalAnd(TypedExpression lhs, TypedExpression rhs)
        {
            // Get the Value for the lhs and rhs
            this.ProcessSubexpression(lhs);
            Value lhsValue = this.SharedState.ValueStack.Pop();
            this.ProcessSubexpression(rhs);
            Value rhsValue = this.SharedState.ValueStack.Pop();

            if (lhs.ResolvedType.Resolution.IsBool)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.And(lhsValue, rhsValue));
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(this.SharedState.CurrentContext.BoolType));
            }

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnLogicalNot(TypedExpression ex)
        {
            // Get the Value for the expression
            this.ProcessSubexpression(ex);
            Value exValue = this.SharedState.ValueStack.Pop();

            if (ex.ResolvedType.Resolution.IsBool)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.Not(exValue));
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(this.SharedState.CurrentContext.BoolType));
            }

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnLogicalOr(TypedExpression lhs, TypedExpression rhs)
        {
            // Get the Value for the lhs and rhs
            this.ProcessSubexpression(lhs);
            Value lhsValue = this.SharedState.ValueStack.Pop();
            this.ProcessSubexpression(rhs);
            Value rhsValue = this.SharedState.ValueStack.Pop();

            if (lhs.ResolvedType.Resolution.IsBool)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.Or(lhsValue, rhsValue));
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(this.SharedState.CurrentContext.BoolType));
            }

            return ResolvedExpression.InvalidExpr;
        }

        //public override ResolvedExpression OnMissingExpression()
        //{
        //    return base.OnMissingExpression();
        //}

        public override ResolvedExpression OnModulo(TypedExpression lhs, TypedExpression rhs)
        {
            this.ProcessSubexpression(lhs);
            Value lhsValue = this.SharedState.ValueStack.Pop();
            this.ProcessSubexpression(rhs);
            Value rhsValue = this.SharedState.ValueStack.Pop();

            if (lhs.ResolvedType.Resolution.IsInt)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.SRem(lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsBigInt)
            {
                var func = this.SharedState.GetRuntimeFunction("bigint_modulus");
                this.SharedState.PushValueInScope(this.SharedState.CurrentBuilder.Call(func, lhsValue, rhsValue),
                    lhs.ResolvedType);
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(lhsValue.NativeType));
            }

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnMultiplication(TypedExpression lhs, TypedExpression rhs)
        {
            this.ProcessSubexpression(lhs);
            Value lhsValue = this.SharedState.ValueStack.Pop();
            this.ProcessSubexpression(rhs);
            Value rhsValue = this.SharedState.ValueStack.Pop();

            if (lhs.ResolvedType.Resolution.IsInt)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.Mul(lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsDouble)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.FMul(lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsBigInt)
            {
                var func = this.SharedState.GetRuntimeFunction("bigint_multiply");
                this.SharedState.PushValueInScope(this.SharedState.CurrentBuilder.Call(func, lhsValue, rhsValue),
                    lhs.ResolvedType);
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(lhsValue.NativeType));
            }

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnNamedItem(TypedExpression ex, Identifier acc)
        {
            var t = ex.ResolvedType;
            if ((t.Resolution is QsResolvedTypeKind.UserDefinedType tt) && 
                this.SharedState.TryFindUDT(tt.Item.Namespace.Value, tt.Item.Name.Value, out QsCustomType udt))
            {
                var location = new List<(int, ITypeRef)>();
                if (this.FindNamedItem((acc as Identifier.LocalVariable).Item.Value, udt.TypeItems, location))
                {
                    // The location list is backwards, by design, so we have to reverse it
                    location.Reverse();
                    var value = this.EvaluateSubexpression(ex);
                    for (int i = 0; i < location.Count; i++)
                    {
                        var indices = new Value[] { this.SharedState.CurrentContext.CreateConstant(0L),
                                                    this.SharedState.CurrentContext.CreateConstant(location[i].Item1) };
                        var ptr = this.SharedState.CurrentBuilder.GetElementPtr((location[i].Item2 as IPointerType).ElementType, value, indices);
#pragma warning disable CS0618 // Computing the correct type for ptr here is awkward, so we don't bother
                        value = this.SharedState.CurrentBuilder.Load(ptr);
#pragma warning restore CS0618 // Type or member is obsolete
                    }
                    this.SharedState.ValueStack.Push(value);
                }
                else
                {
                    this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(this.SharedState.QirInt));
                }
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(this.SharedState.QirInt));
            }

            return ResolvedExpression.InvalidExpr;
        }

        private bool FindNamedItem(string name, QsTuple<QsTypeItem> items, List<(int, ITypeRef)> location)
        {
            ITypeRef GetTypeItemType(QsTuple<QsTypeItem> item)
            {
                switch (item)
                {
                    case QsTuple<QsTypeItem>.QsTupleItem leaf:
                        var leafType = leaf.Item switch
                        {
                            QsTypeItem.Anonymous anon => anon.Item,
                            QsTypeItem.Named named => named.Item.Type,
                            _ => ResolvedType.New(QsResolvedTypeKind.InvalidType)
                        };
                        return this.SharedState.LlvmTypeFromQsharpType(leafType);
                    case QsTuple<QsTypeItem>.QsTuple list:
                        var types = list.Item.Select(i => i switch
                        {
                            QsTuple<QsTypeItem>.QsTuple l => GetTypeItemType(l),
                            QsTuple<QsTypeItem>.QsTupleItem l => GetTypeItemType(l),
                            _ => this.SharedState.CurrentContext.TokenType
                        }).ToArray();
                        return this.SharedState.CurrentContext.CreateStructType(false, this.SharedState.QirTupleHeader,
                            types).CreatePointerType();
                    default:
                        // This should never happen
                        return this.SharedState.CurrentContext.TokenType;
                }
            }

            switch (items)
            {
                case QsTuple<QsTypeItem>.QsTupleItem leaf:
                    if ((leaf.Item is QsTypeItem.Named n) && (n.Item.VariableName.Value == name))
                    {
                        return true;
                    }
                    break;
                case QsTuple<QsTypeItem>.QsTuple list:
                    for (int i = 0; i < list.Item.Length; i++)
                    {
                        if (this.FindNamedItem(name, list.Item[i], location))
                        {
                            // +1 to skip the tuple header
                            location.Add((i + 1, GetTypeItemType(items)));
                            return true;
                        }
                    }
                    break;
            }
            return false;
        }

        public override ResolvedExpression OnNegative(TypedExpression ex)
        {
            this.ProcessSubexpression(ex);
            Value exValue = this.SharedState.ValueStack.Pop();

            if (ex.ResolvedType.Resolution.IsInt)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.Neg(exValue));
            }
            else if (ex.ResolvedType.Resolution.IsDouble)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.FNeg(exValue));
            }
            else if (ex.ResolvedType.Resolution.IsBigInt)
            {
                var func = this.SharedState.GetRuntimeFunction("bigint_negate");
                this.SharedState.PushValueInScope(this.SharedState.CurrentBuilder.Call(func, exValue), ex.ResolvedType);
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(exValue.NativeType));
            }

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnNewArray(ResolvedType elementType, TypedExpression idx)
        {
            // TODO: new multi-dimensional arrays
            var elementSize = this.ComputeSizeForType(elementType);

            this.ProcessSubexpression(idx);
            var length = this.SharedState.ValueStack.Pop();

            var createFunc = this.SharedState.GetRuntimeFunction("array_create_1d");
            var array = this.SharedState.CurrentBuilder.Call(createFunc,
                this.SharedState.CurrentContext.CreateConstant(elementSize), length);
            this.SharedState.PushValueInScope(array, ResolvedType.New(QsResolvedTypeKind.NewArrayType(elementType)));

            return ResolvedExpression.InvalidExpr;
        }

        //public override ResolvedExpression OnOperationCall(TypedExpression method, TypedExpression arg)
        //{
        //    return base.OnOperationCall(method, arg);
        //}

        //public override ResolvedExpression OnPartialApplication(TypedExpression method, TypedExpression arg)
        //{
        //    return base.OnPartialApplication(method, arg);
        //}

        public override ResolvedExpression OnPauliLiteral(QsPauli p)
        {
            if (p.IsPauliI)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.Load(this.SharedState.QirPauli,
                    this.SharedState.QirPauliI));
            }
            else if (p.IsPauliX)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.Load(this.SharedState.QirPauli,
                    this.SharedState.QirPauliX));
            }
            else if (p.IsPauliY)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.Load(this.SharedState.QirPauli,
                    this.SharedState.QirPauliY));
            }
            else if (p.IsPauliZ)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.Load(this.SharedState.QirPauli,
                    this.SharedState.QirPauliZ));
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(this.SharedState.QirPauli));
            }

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnRangeLiteral(TypedExpression lhs, TypedExpression rhs)
        {
            Value start;
            Value step;

            switch (lhs.Expression)
            {
                case ResolvedExpression.RangeLiteral lit:
                    start = EvaluateSubexpression(lit.Item1);
                    step = EvaluateSubexpression(lit.Item2);
                    break;
                default:
                    start = EvaluateSubexpression(lhs);
                    step = SharedState.CurrentContext.CreateConstant(1L);
                    break;
            }

            var end = this.EvaluateSubexpression(rhs);

            Value rangePtr = this.SharedState.QirEmptyRange;
            Value range = this.SharedState.CurrentBuilder.Load(this.SharedState.QirRange, rangePtr);
            range = this.SharedState.CurrentBuilder.InsertValue(range, start, 0);
            range = this.SharedState.CurrentBuilder.InsertValue(range, step, 1);
            range = this.SharedState.CurrentBuilder.InsertValue(range, end, 2);
            this.SharedState.ValueStack.Push(range);

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnResultLiteral(QsResult r)
        {
            var valuePtr = r.IsOne ? this.SharedState.QirResultOne : this.SharedState.QirResultZero;
            var value = this.SharedState.CurrentBuilder.Load(this.SharedState.QirResult, valuePtr);
            this.SharedState.ValueStack.Push(value);

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnRightShift(TypedExpression lhs, TypedExpression rhs)
        {
            this.ProcessSubexpression(lhs);
            Value lhsValue = this.SharedState.ValueStack.Pop();
            this.ProcessSubexpression(rhs);
            Value rhsValue = this.SharedState.ValueStack.Pop();

            if (lhs.ResolvedType.Resolution.IsInt)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.ArithmeticShiftRight(lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsBigInt)
            {
                var func = this.SharedState.GetRuntimeFunction("bigint_shiftright");
                this.SharedState.PushValueInScope(this.SharedState.CurrentBuilder.Call(func, lhsValue, rhsValue),
                    lhs.ResolvedType);
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(lhsValue.NativeType));
            }

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnStringLiteral(NonNullable<string> s, ImmutableArray<TypedExpression> exs)
        {
            Value CreateConstantString(string s)
            {
                // Deal with escape sequences: \{, \\, \n, \r, \t, \". This is not an efficient
                // way to do this, but it's simple and clear, and strings are uncommon in Q#.
                var cleanStr = s.Replace("\\{", "{").Replace("\\\\", "\\").Replace("\\n", "\n")
                    .Replace("\\r", "\r").Replace("\\t", "\t").Replace("\\\"", "\"");
                var constantString = this.SharedState.CurrentContext.CreateConstantString(cleanStr);
                var zeroLengthString = this.SharedState.CurrentBuilder.BitCast(constantString,
                    this.SharedState.CurrentContext.Int8Type.CreateArrayType(0));
                var n = this.SharedState.CurrentContext.CreateConstant(s.Length);
                var stringValue = this.SharedState.CurrentBuilder.Call(
                    this.SharedState.GetRuntimeFunction("string_create"), n, zeroLengthString);
                return stringValue;
            }

            Value SimpleToString(TypedExpression ex, string rtFuncName)
            {
                var exValue = this.EvaluateSubexpression(ex);
                var stringValue = this.SharedState.CurrentBuilder.Call(
                    this.SharedState.GetRuntimeFunction(rtFuncName), exValue);
                return stringValue;
            }

            Value ExpressionToString(TypedExpression ex)
            {
                var ty = ex.ResolvedType.Resolution;
                if (ty.IsString)
                {
                    // Special case -- if this is the value of an identifier, we need to increment
                    // it's reference count
                    var s = this.EvaluateSubexpression(ex);
                    if (ex.Expression.IsIdentifier)
                    {
                        var stringValue = this.SharedState.CurrentBuilder.Call(
                            this.SharedState.GetRuntimeFunction("string_reference"), s);
                    }
                    return s;
                }
                else if (ty.IsBigInt)
                {
                    return SimpleToString(ex, "bigint_to_string");
                }
                else if (ty.IsBool)
                {
                    return SimpleToString(ex, "bool_to_string");
                }
                else if (ty.IsInt)
                {
                    return SimpleToString(ex, "int_to_string");
                }
                else if (ty.IsResult)
                {
                    return SimpleToString(ex, "result_to_string");
                }
                else if (ty.IsPauli)
                {
                    return SimpleToString(ex, "pauli_to_string");
                }
                else if (ty.IsQubit)
                {
                    return SimpleToString(ex, "qubit_to_string");
                }
                else if (ty.IsRange)
                {
                    return SimpleToString(ex, "range_to_string");
                }
                else if (ty.IsDouble)
                {
                    return SimpleToString(ex, "double_to_string");
                }
                else if (ty.IsFunction)
                {
                    return CreateConstantString("<function>");
                }
                else if (ty.IsOperation)
                {
                    return CreateConstantString("<operation>");
                }
                else if (ty.IsUnitType)
                {
                    return CreateConstantString("()");
                }
                else if (ty.IsArrayType)
                {
                    // TODO: Do something better for array-to-string
                    return CreateConstantString("[...]");
                }
                else if (ty.IsTupleType)
                {
                    // TODO: Do something better for tuple-to-string
                    return CreateConstantString("(...)");
                }
                else if (ty.IsUserDefinedType)
                {
                    // TODO: Do something better for UDT-to-string
                    var udtName = (ty as QsResolvedTypeKind.UserDefinedType).Item.Name.Value;
                    return CreateConstantString(udtName + "(...)");
                }
                else
                {
                    return CreateConstantString("...");
                }
            }

            static (int, int, int) FindNextExpression(string s, int start)
            {
                while (true)
                {
                    var i = s.IndexOf('{', start);
                    if (i < 0)
                    {
                        return (-1, s.Length, -1);
                    }
                    else if ((i == start) || (s[i-1] != '\\'))
                    {
                        var j = s.IndexOf('}', i + 1);
                        if (j < 0)
                        {
                            throw new FormatException("Missing } in interpolated string");
                        }
                        var n = int.Parse(s[(i + 1)..j]);
                        return (i, j + 1, n);
                    }
                    start = i + 1;
                }
            }

            // We need to be careful here to unreference intermediate strings, but not the final value
            Value DoAppend(Value curr, Value next)
            {
                if (curr == null)
                {
                    return next;
                }
                else
                {
                    var app = this.SharedState.CurrentBuilder.Call(
                        this.SharedState.GetRuntimeFunction("string_concatenate"), curr, next);
                    // Unreference the component strings
                    this.SharedState.CurrentBuilder.Call(
                        this.SharedState.GetRuntimeFunction("string_unreference"), curr);
                    this.SharedState.CurrentBuilder.Call(
                        this.SharedState.GetRuntimeFunction("string_unreference"), next);
                    return app;
                }
            }

            if (exs.IsEmpty)
            {
                var stringValue = CreateConstantString(s.Value);
                this.SharedState.ValueStack.Push(stringValue);
                this.SharedState.ScopeMgr.AddValue(stringValue, ResolvedType.New(QsResolvedTypeKind.String));
            }
            else
            {
                // Compiled interpolated strings look like <text>{<int>}<text>...
                // Our basic pattern is to scan for the next '{', append the intervening text if any
                // as a constant string, scan for the closing '}', parse out the integer in between, 
                // evaluate the corresponding expression, append it, and keep going.
                // We do have to be a little careful because we can't just look for '{', we have to
                // make sure we skip escaped braces -- "\{".
                Value current = null;
                var offset = 0;
                var str = s.Value;
                while (offset < str.Length)
                {
                    var (end, next, index) = FindNextExpression(str, offset);
                    if (end < 0)
                    {
                        var last = CreateConstantString(str.Substring(offset));
                        current = DoAppend(current, last);
                        break;
                    }
                    else
                    {
                        if (end > offset)
                        {
                            var last = CreateConstantString(str[offset..end]);
                            current = DoAppend(current, last);
                        }
                        if (index >= 0)
                        {
                            var exString = ExpressionToString(exs[index]);
                            current = DoAppend(current, exString);
                        }

                        offset = next;
                    }
                }
                this.SharedState.ValueStack.Push(current);
                this.SharedState.ScopeMgr.AddValue(current, ResolvedType.New(QsResolvedTypeKind.String));
            }

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnSubtraction(TypedExpression lhs, TypedExpression rhs)
        {
            this.ProcessSubexpression(lhs);
            Value lhsValue = this.SharedState.ValueStack.Pop();
            this.ProcessSubexpression(rhs);
            Value rhsValue = this.SharedState.ValueStack.Pop();

            if (lhs.ResolvedType.Resolution.IsInt)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.Sub(lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsDouble)
            {
                this.SharedState.ValueStack.Push(this.SharedState.CurrentBuilder.FSub(lhsValue, rhsValue));
            }
            else if (lhs.ResolvedType.Resolution.IsBigInt)
            {
                var func = this.SharedState.GetRuntimeFunction("bigint_subtract");
                this.SharedState.PushValueInScope(this.SharedState.CurrentBuilder.Call(func, lhsValue, rhsValue),
                    lhs.ResolvedType);
            }
            else
            {
                this.SharedState.ValueStack.Push(Constant.UndefinedValueFor(lhsValue.NativeType));
            }

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnUnitValue()
        {
            this.SharedState.ValueStack.Push(this.SharedState.QirTuplePointer.GetNullValue());

            return ResolvedExpression.InvalidExpr;
        }

        // We don't need to do anything for unwrap since we represent all UDT values as tuples.
        //public override ResolvedExpression OnUnwrapApplication(TypedExpression ex)
        //{
        //    return base.OnUnwrapApplication(ex);
        //}

        public override ResolvedExpression OnValueArray(ImmutableArray<TypedExpression> vs)
        {
            // TODO: handle multi-dimensional arrays
            long length = vs.Length;

            // Get the element type
            var elementType = vs[0].ResolvedType;
            var elementTypeRef = this.SharedState.LlvmTypeFromQsharpType(elementType);
            var elementPointerTypeRef = elementTypeRef.CreatePointerType();
            var elementSize = this.ComputeSizeForType(elementType);

            var createFunc = this.SharedState.GetRuntimeFunction("array_create_1d");
            var array = this.SharedState.CurrentBuilder.Call(createFunc,
                this.SharedState.CurrentContext.CreateConstant(elementSize),
                this.SharedState.CurrentContext.CreateConstant(length));

            long idx = 0;
            foreach (var element in vs)
            {
                var pointer = this.SharedState.CurrentBuilder.Call(this.SharedState.GetRuntimeFunction("array_get_element_ptr_1d"),
                    array, this.SharedState.CurrentContext.CreateConstant(idx));

                // And now fill in the element
                this.ProcessSubexpression(element);
                var elementValue = this.SharedState.ValueStack.Pop();
                var elementPointer = this.SharedState.CurrentBuilder.BitCast(pointer, elementPointerTypeRef);
                this.SharedState.CurrentBuilder.Store(elementValue, elementPointer);
                idx++;
            }

            this.SharedState.PushValueInScope(array, ResolvedType.New(QsResolvedTypeKind.NewArrayType(elementType)));

            return ResolvedExpression.InvalidExpr;
        }

        public override ResolvedExpression OnValueTuple(ImmutableArray<TypedExpression> vs)
        {
            // Build the LLVM structure type we need
            var rest = vs.Select(v => this.SharedState.LlvmTypeFromQsharpType(v.ResolvedType)).ToArray();
            var tupleType = this.SharedState.CurrentContext.CreateStructType(false, this.SharedState.QirTupleHeader, rest);

            // Allocate the tuple and record it to get released later
            var tupleHeaderPointer = this.SharedState.CreateTupleForType(tupleType);
            var tuplePointer = this.SharedState.CurrentBuilder.BitCast(tupleHeaderPointer, tupleType.CreatePointerType());
            this.SharedState.PushValueInScope(tuplePointer,
                ResolvedType.New(QsResolvedTypeKind.NewTupleType(vs.Select(v => v.ResolvedType).ToImmutableArray())));

            // Fill it in, field by field; we want iteri, which is easiest with a simple for loop
            for (int i = 0; i < vs.Length; i++)
            {
                var itemValue = this.EvaluateSubexpression(vs[i]);
                var itemPointer = this.SharedState.GetTupleElementPointer(tupleType, tuplePointer, i + 1);
                this.SharedState.CurrentBuilder.Store(itemValue, itemPointer);
            }

            return ResolvedExpression.InvalidExpr;
        }

        /// <summary>
        /// Processes an expression and leave its Value on top of the shared value stack.
        /// </summary>
        /// <param name="ex">The expression to process</param>
        private void ProcessSubexpression(TypedExpression ex)
        {
            this.Transformation.Expressions.OnTypedExpression(ex);
        }

        /// <summary>
        /// Processes an expression and returns its Value.
        /// </summary>
        /// <param name="ex">The expression to process</param>
        /// <returns>The LLVM Value that represents the result of the expression</returns>
        internal Value EvaluateSubexpression(TypedExpression ex)
        {
            this.Transformation.Expressions.OnTypedExpression(ex);
            return this.SharedState.ValueStack.Pop();
        }

        /// <summary>
        /// Returns the number of bytes required for a value of the given type when stored as an element in an array.
        /// Note that non-scalar values all wind up as pointers.
        /// </summary>
        /// <param name="t">The Q# type of the array elements</param>
        /// <returns>The number of bytes required per element</returns>
        private int ComputeSizeForType(ResolvedType t)
        {
            // Sizes in bytes
            // Assumes addresses are 64 bits wide
            if (t.Resolution.IsBool || t.Resolution.IsPauli)
            {
                return 1;
            }
            else if (t.Resolution.IsInt)
            {
                return 8;
            }
            else if (t.Resolution.IsDouble)
            {
                return 16;
            }
            else if (t.Resolution.IsRange)
            {
                return 24;
            }
            // Everything else is a pointer...
            else
            {
                return 8;
            }
        }
    }
}
