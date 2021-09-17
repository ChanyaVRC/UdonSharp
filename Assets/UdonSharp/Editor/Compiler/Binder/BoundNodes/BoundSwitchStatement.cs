﻿using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using UdonSharp.Compiler.Assembly;
using UdonSharp.Compiler.Emit;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.Compiler.Binder
{
    internal sealed class BoundSwitchStatement : BoundStatement
    {
        private BoundExpression SwitchExpression { get; }
        private List<(List<BoundExpression>, List<BoundStatement>)> SwitchSections { get; }
        
        private int DefaultSectionIdx { get; }
        
        public BoundSwitchStatement(SyntaxNode node, BoundExpression expression, List<(List<BoundExpression>, List<BoundStatement>)> sections, int defaultSectionIdx)
            :base(node)
        {
            SwitchExpression = expression;
            SwitchSections = sections;
            DefaultSectionIdx = defaultSectionIdx;
        }

        public override void Emit(EmitContext context)
        {
            // Todo: look at adding binary search and then dictionary lookups as fallbacks for especially large switches that can't use jump tables
            if (IsJumpTableCandidate())
                EmitJumpTableSwitchStatement(context);
            else
                EmitDefaultSwitchStatement(context);
        }

        private void EmitDefaultSwitchStatement(EmitContext context)
        {
            JumpLabel breakLabel = context.PushBreakLabel();
            JumpLabel defaultLabel = context.Module.CreateLabel();

            Value switchConditionVal = context.EmitValue(SwitchExpression);
            var conditionAccess = BoundAccessExpression.BindAccess(switchConditionVal);

            JumpLabel nextLabel = context.Module.CreateLabel();

            using (context.OpenBlockScope())
            {
                for (int i = 0; i < SwitchSections.Count; ++i)
                {
                    var section = SwitchSections[i];
                    JumpLabel sectionBodyLabel = context.Module.CreateLabel();

                    foreach (var labelExpression in section.Item1)
                    {
                        context.Module.LabelJump(nextLabel);

                        nextLabel = context.Module.CreateLabel();
                        
                        Value conditionCheck = context.EmitValue(BoundInvocationExpression.CreateBoundInvocation(
                            context, SyntaxNode,
                            new ExternSynthesizedOperatorSymbol(BuiltinOperatorType.Equality,
                                switchConditionVal.UdonType, context), null,
                            new [] { conditionAccess, labelExpression }));
                        
                        context.Module.AddJumpIfFalse(nextLabel, conditionCheck);
                        
                        if (section.Item1.Count > 1)
                            context.Module.AddJump(sectionBodyLabel);
                    }
                    
                    if (i == DefaultSectionIdx)
                        context.Module.LabelJump(defaultLabel);
                    
                    context.Module.LabelJump(sectionBodyLabel);

                    foreach (var statement in section.Item2)
                    {
                        context.Emit(statement);
                    }
                }
                
                context.Module.LabelJump(nextLabel);
                
                if (DefaultSectionIdx != -1)
                    context.Module.AddJump(defaultLabel);
                
                context.Module.LabelJump(breakLabel);
            }
            
            context.PopBreakLabel();
        }

        private void EmitJumpTableSwitchStatement(EmitContext context)
        {
            Value expressionValue = context.EmitValue(SwitchExpression);

            JumpLabel exitLabel = context.PushBreakLabel();
            JumpLabel defaultJump = context.Module.CreateLabel();

            int maxValue = 0;
            foreach (var switchSection in SwitchSections)
            {
                foreach (var expression in switchSection.Item1)
                    maxValue = Math.Max(maxValue, Convert.ToInt32(expression.ConstantValue.Value));
            }

            Value greaterThanZeroCondition = context.EmitValue(BoundInvocationExpression.CreateBoundInvocation(
                context, SyntaxNode,
                new ExternSynthesizedOperatorSymbol(BuiltinOperatorType.GreaterThanOrEqual,
                    expressionValue.UdonType, context), null,
                new[]
                {
                    BoundAccessExpression.BindAccess(expressionValue),
                    BoundAccessExpression.BindAccess(context.GetConstantValue(expressionValue.UdonType,
                        Convert.ChangeType(0, expressionValue.UdonType.SystemType)))
                }));
            
            context.Module.AddJumpIfFalse(defaultJump, greaterThanZeroCondition);
            
            Value lessThanMaxCondition = context.EmitValue(BoundInvocationExpression.CreateBoundInvocation(
                context, SyntaxNode,
                new ExternSynthesizedOperatorSymbol(BuiltinOperatorType.LessThanOrEqual,
                    expressionValue.UdonType, context), null,
                new[]
                {
                    BoundAccessExpression.BindAccess(expressionValue),
                    BoundAccessExpression.BindAccess(context.GetConstantValue(expressionValue.UdonType,
                        Convert.ChangeType(maxValue, expressionValue.UdonType.SystemType)))
                }));
            
            context.Module.AddJumpIfFalse(defaultJump, lessThanMaxCondition);

            Value convertedValue = context.CastValue(expressionValue, context.GetTypeSymbol(SpecialType.System_Int32), true);
            Value jumpTable = context.CreateGlobalInternalValue(context.GetTypeSymbol(SpecialType.System_UInt32).MakeArrayType(context));
            Value jumpAddress = context.EmitValue(BoundAccessExpression.BindElementAccess(context, SyntaxNode,
                BoundAccessExpression.BindAccess(jumpTable),
                new BoundExpression[] { BoundAccessExpression.BindAccess(convertedValue) }));
            
            context.Module.AddJumpIndrect(jumpAddress);

            uint[] jumpTableArr = new uint[maxValue + 1];

            using (context.OpenBlockScope())
            {
                for (int i = 0; i < SwitchSections.Count; ++i)
                {
                    var switchSection = SwitchSections[i];
                    
                    JumpLabel currentPos = context.Module.CreateLabel();
                    context.Module.LabelJump(currentPos);
                    if (DefaultSectionIdx == i)
                        context.Module.LabelJump(defaultJump);
                    
                    foreach (var labelExpression in switchSection.Item1)
                    {
                        int labelIdx = Convert.ToInt32(labelExpression.ConstantValue.Value);
                        jumpTableArr[labelIdx] = currentPos.Address;
                    }

                    foreach (var statement in switchSection.Item2)
                    {
                        context.Emit(statement);
                    }
                }
            }
            
            if (DefaultSectionIdx == -1)
                context.Module.LabelJump(defaultJump);
            
            context.Module.LabelJump(exitLabel);
            
            context.PopBreakLabel();

            for (int i = 0; i < jumpTableArr.Length; ++i)
            {
                if (jumpTableArr[i] == 0)
                    jumpTableArr[i] = defaultJump.Address;
            }

            jumpTable.DefaultValue = jumpTableArr;
        }

        private const int JUMP_TABLE_MAX = 256;

        private bool IsJumpTableCandidate()
        {
            if (!UdonSharpUtils.IsIntegerType(SwitchExpression.ValueType.UdonType.SystemType))
                return false;

            int labelCount = 0;
            
            foreach (var switchSection in SwitchSections)
            {
                foreach (var expression in switchSection.Item1)
                {
                    labelCount++;

                    if (expression.ConstantValue.Value is ulong ulongVal && (ulongVal > JUMP_TABLE_MAX))
                        return false;

                    long intVal = Convert.ToInt64(expression.ConstantValue.Value);

                    if (intVal > JUMP_TABLE_MAX || intVal < 0)
                        return false;
                }
            }

            if (labelCount < 4)
                return false;

            return true;
        }
    }
}