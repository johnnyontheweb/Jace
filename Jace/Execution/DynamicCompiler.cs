﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Jace.Operations;
using Jace.Util;

namespace Jace.Execution
{
    public class DynamicCompiler : IExecutor
    {
        private string FuncAssemblyQualifiedName;
        private readonly bool adjustVariableCaseEnabled;

        public DynamicCompiler(): this(true) { }
        public DynamicCompiler(bool adjustVariableCaseEnabled)
        {
            this.adjustVariableCaseEnabled = adjustVariableCaseEnabled;
            // The lower func reside in mscorelib, the higher ones in another assembly.
            // This is  an easy cross platform way to to have this AssemblyQualifiedName.
            FuncAssemblyQualifiedName =
                typeof(Func<double, double, double, double, double, double, double, double, double, double>).Assembly.FullName;
        }

        public double Execute(Operation operation, IFunctionRegistry functionRegistry, IConstantRegistry constantRegistry)
        {
            return Execute(operation, functionRegistry, constantRegistry, new Dictionary<string, double>());
        }

        public double Execute(Operation operation, IFunctionRegistry functionRegistry, IConstantRegistry constantRegistry, 
            IDictionary<string, double> variables)
        {
            return BuildFormula(operation, functionRegistry, constantRegistry, variables)(variables);
        }

        public Func<IDictionary<string, double>, double> BuildFormula(Operation operation,
            IFunctionRegistry functionRegistry, IConstantRegistry constantRegistry, IDictionary<string, double> vars)
        {
            Func<FormulaContext, double> func = BuildFormulaInternal(operation, functionRegistry, vars);
            return adjustVariableCaseEnabled
                ? (Func<IDictionary<string, double>, double>)(variables =>
                {
                  variables = EngineUtil.ConvertVariableNamesToLowerCase(variables);
                  FormulaContext context = new FormulaContext(variables, functionRegistry, constantRegistry);
                  return func(context);
                })
                : (Func<IDictionary<string, double>, double>)(variables =>
                {
                  return func(new FormulaContext(variables, functionRegistry, constantRegistry));
                });
        }

        private Func<FormulaContext, double> BuildFormulaInternal(Operation operation, 
            IFunctionRegistry functionRegistry, IDictionary<string, double> variables)
        {
            ParameterExpression contextParameter = Expression.Parameter(typeof(FormulaContext), "context");

            LabelTarget returnLabel = Expression.Label(typeof(double));

            Expression<Func<FormulaContext, double>> lambda = Expression.Lambda<Func<FormulaContext, double>>(
                GenerateMethodBody(operation, contextParameter, functionRegistry, variables),
                contextParameter
            );
            return lambda.Compile();
        }

        

        private Expression GenerateMethodBody(Operation operation, ParameterExpression contextParameter,
            IFunctionRegistry functionRegistry, IDictionary<string, double> variables)
        {
            if (operation == null)
                throw new ArgumentNullException("operation");

            if (operation.GetType() == typeof(IntegerConstant))
            {
                IntegerConstant constant = (IntegerConstant)operation;

                double value = constant.Value;
                return Expression.Constant(value, typeof(double));
            }
            else if (operation.GetType() == typeof(FloatingPointConstant))
            {
                FloatingPointConstant constant = (FloatingPointConstant)operation;

                return Expression.Constant(constant.Value, typeof(double));
            }
            else if (operation.GetType() == typeof(Variable))
            {
                Variable variable = (Variable)operation;

                Func<string, FormulaContext, double> getVariableValueOrThrow = PrecompiledMethods.GetVariableValueOrThrow;
                return Expression.Call(null,
                    getVariableValueOrThrow.Method,
                    Expression.Constant(variable.Name),
                    contextParameter);
            }
            else if (operation.GetType() == typeof(Multiplication))
            {
                Multiplication multiplication = (Multiplication)operation;
                Expression argument1 = GenerateMethodBody(multiplication.Argument1, contextParameter, functionRegistry, variables);
                Expression argument2 = GenerateMethodBody(multiplication.Argument2, contextParameter, functionRegistry, variables);

                return Expression.Multiply(argument1, argument2);
            }
            else if (operation.GetType() == typeof(Addition))
            {
                Addition addition = (Addition)operation;
                Expression argument1 = GenerateMethodBody(addition.Argument1, contextParameter, functionRegistry, variables);
                Expression argument2 = GenerateMethodBody(addition.Argument2, contextParameter, functionRegistry, variables);

                return Expression.Add(argument1, argument2);
            }
            else if (operation.GetType() == typeof(Subtraction))
            {
                Subtraction addition = (Subtraction)operation;
                Expression argument1 = GenerateMethodBody(addition.Argument1, contextParameter, functionRegistry, variables);
                Expression argument2 = GenerateMethodBody(addition.Argument2, contextParameter, functionRegistry, variables);

                return Expression.Subtract(argument1, argument2);
            }
            else if (operation.GetType() == typeof(Division))
            {
                Division division = (Division)operation;
                Expression dividend = GenerateMethodBody(division.Dividend, contextParameter, functionRegistry, variables);
                Expression divisor = GenerateMethodBody(division.Divisor, contextParameter, functionRegistry, variables);

                return Expression.Divide(dividend, divisor);
            }
            else if (operation.GetType() == typeof(Modulo))
            {
                Modulo modulo = (Modulo)operation;
                Expression dividend = GenerateMethodBody(modulo.Dividend, contextParameter, functionRegistry, variables);
                Expression divisor = GenerateMethodBody(modulo.Divisor, contextParameter, functionRegistry, variables);

                return Expression.Modulo(dividend, divisor);
            }
            else if (operation.GetType() == typeof(Exponentiation))
            {
                Exponentiation exponentation = (Exponentiation)operation;
                Expression @base = GenerateMethodBody(exponentation.Base, contextParameter, functionRegistry, variables);
                Expression exponent = GenerateMethodBody(exponentation.Exponent, contextParameter, functionRegistry, variables);

                return Expression.Call(null, typeof(Math).GetMethod("Pow", new Type[] { typeof(double), typeof(double) }), @base, exponent);
            }
            else if (operation.GetType() == typeof(UnaryMinus))
            {
                UnaryMinus unaryMinus = (UnaryMinus)operation;
                Expression argument = GenerateMethodBody(unaryMinus.Argument, contextParameter, functionRegistry, variables);
                return Expression.Negate(argument);
            }
            else if (operation.GetType() == typeof(And))
            {
                And and = (And)operation;
                Expression argument1 = Expression.NotEqual(GenerateMethodBody(and.Argument1, contextParameter, functionRegistry, variables), Expression.Constant(0.0));
                Expression argument2 = Expression.NotEqual(GenerateMethodBody(and.Argument2, contextParameter, functionRegistry, variables), Expression.Constant(0.0));

                return Expression.Condition(Expression.And(argument1, argument2),
                    Expression.Constant(1.0),
                    Expression.Constant(0.0));
            }
            else if (operation.GetType() == typeof(Or))
            {
                Or and = (Or)operation;
                Expression argument1 = Expression.NotEqual(GenerateMethodBody(and.Argument1, contextParameter, functionRegistry, variables), Expression.Constant(0.0));
                Expression argument2 = Expression.NotEqual(GenerateMethodBody(and.Argument2, contextParameter, functionRegistry, variables), Expression.Constant(0.0));

                return Expression.Condition(Expression.Or(argument1, argument2),
                    Expression.Constant(1.0),
                    Expression.Constant(0.0));
            }
            else if (operation.GetType() == typeof(LessThan))
            {
                LessThan lessThan = (LessThan)operation;
                Expression argument1 = GenerateMethodBody(lessThan.Argument1, contextParameter, functionRegistry, variables);
                Expression argument2 = GenerateMethodBody(lessThan.Argument2, contextParameter, functionRegistry, variables);

                return Expression.Condition(Expression.LessThan(argument1, argument2),
                    Expression.Constant(1.0),
                    Expression.Constant(0.0));
            }
            else if (operation.GetType() == typeof(LessOrEqualThan))
            {
                LessOrEqualThan lessOrEqualThan = (LessOrEqualThan)operation;
                Expression argument1 = GenerateMethodBody(lessOrEqualThan.Argument1, contextParameter, functionRegistry, variables);
                Expression argument2 = GenerateMethodBody(lessOrEqualThan.Argument2, contextParameter, functionRegistry, variables);

                return Expression.Condition(Expression.LessThanOrEqual(argument1, argument2),
                    Expression.Constant(1.0),
                    Expression.Constant(0.0));
            }
            else if (operation.GetType() == typeof(GreaterThan))
            {
                GreaterThan greaterThan = (GreaterThan)operation;
                Expression argument1 = GenerateMethodBody(greaterThan.Argument1, contextParameter, functionRegistry, variables);
                Expression argument2 = GenerateMethodBody(greaterThan.Argument2, contextParameter, functionRegistry, variables);

                return Expression.Condition(Expression.GreaterThan(argument1, argument2),
                    Expression.Constant(1.0),
                    Expression.Constant(0.0));
            }
            else if (operation.GetType() == typeof(GreaterOrEqualThan))
            {
                GreaterOrEqualThan greaterOrEqualThan = (GreaterOrEqualThan)operation;
                Expression argument1 = GenerateMethodBody(greaterOrEqualThan.Argument1, contextParameter, functionRegistry, variables);
                Expression argument2 = GenerateMethodBody(greaterOrEqualThan.Argument2, contextParameter, functionRegistry, variables);

                return Expression.Condition(Expression.GreaterThanOrEqual(argument1, argument2),
                    Expression.Constant(1.0),
                    Expression.Constant(0.0));
            }
            else if (operation.GetType() == typeof(Equal))
            {
                Equal equal = (Equal)operation;
                Expression argument1 = GenerateMethodBody(equal.Argument1, contextParameter, functionRegistry, variables);
                Expression argument2 = GenerateMethodBody(equal.Argument2, contextParameter, functionRegistry, variables);

                return Expression.Condition(Expression.Equal(argument1, argument2),
                    Expression.Constant(1.0),
                    Expression.Constant(0.0));
            }
            else if (operation.GetType() == typeof(NotEqual))
            {
                NotEqual notEqual = (NotEqual)operation;
                Expression argument1 = GenerateMethodBody(notEqual.Argument1, contextParameter, functionRegistry, variables);
                Expression argument2 = GenerateMethodBody(notEqual.Argument2, contextParameter, functionRegistry, variables);

                return Expression.Condition(Expression.NotEqual(argument1, argument2),
                    Expression.Constant(1.0),
                    Expression.Constant(0.0));
            }
            else if (operation.GetType() == typeof(Function))
            {
                Function function = (Function)operation;

                FunctionInfo functionInfo = functionRegistry.GetFunctionInfo(function.FunctionName);
                Type funcType;
                Type[] parameterTypes;
                Expression[] arguments;

                if (function.FunctionName == "isvardefined")
                {
                    if (function.Arguments.Count != 1)
                        throw new ArgumentException("Invalid number of arguments.");
                    if (function.Arguments[0].GetType() == typeof(Variable))
                    {
                        Variable variable = (Variable)function.Arguments[0];
                        double value;
                        bool variableFound = variables.TryGetValue(variable.Name, out value);

                        IntegerConstant newArgument = new IntegerConstant(variableFound ? 1 : 0);
                        function.Arguments[0] = newArgument;
                    }
                    else if (function.Arguments[0].GetType() == typeof(IntegerConstant))
                    {
                        IntegerConstant oldArgument = (IntegerConstant)function.Arguments[0];
                        IntegerConstant newArgument = new IntegerConstant(oldArgument.Value != 0 ? 1 : 0);
                        function.Arguments[0] = newArgument;
                    }
                    else if (function.Arguments[0].GetType() == typeof(FloatingPointConstant))
                    {
                        FloatingPointConstant oldArgument = (FloatingPointConstant)function.Arguments[0];
                        FloatingPointConstant newArgument = new FloatingPointConstant(oldArgument.Value != 0 ? 1 : 0);
                        function.Arguments[0] = newArgument;
                    }
                    else
                    {
                        throw new ArgumentException("Invalid argument for isVarDefined.");
                    }
                }

                if (functionInfo.IsDynamicFunc)
                {
                    funcType = typeof(DynamicFunc<double, double>);
                    parameterTypes = new Type[] { typeof(double[]) };


                    Expression[] arrayArguments = new Expression[function.Arguments.Count];
                    for (int i = 0; i < function.Arguments.Count; i++)
                        arrayArguments[i] = GenerateMethodBody(function.Arguments[i], contextParameter, functionRegistry, variables);

                    arguments = new Expression[1];
                    arguments[0] = NewArrayExpression.NewArrayInit(typeof(double), arrayArguments);
                }
                else
                {
                    funcType = GetFuncType(functionInfo.NumberOfParameters);
                    parameterTypes = (from i in Enumerable.Range(0, functionInfo.NumberOfParameters)
                                             select typeof(double)).ToArray();

                    arguments = new Expression[functionInfo.NumberOfParameters];
                    for (int i = 0; i < functionInfo.NumberOfParameters; i++)
                        arguments[i] = GenerateMethodBody(function.Arguments[i], contextParameter, functionRegistry, variables);
                }

                Expression getFunctionRegistry = Expression.Property(contextParameter, "FunctionRegistry");

                ParameterExpression functionInfoVariable = Expression.Variable(typeof(FunctionInfo));

                Expression funcInstance;
                if (!functionInfo.IsOverWritable)
                {
                    funcInstance = Expression.Convert(
                        Expression.Property(
                            Expression.Call(
                                getFunctionRegistry,
                                typeof(IFunctionRegistry).GetMethod("GetFunctionInfo", new Type[] { typeof(string) }),
                                Expression.Constant(function.FunctionName)),
                            "Function"),
                        funcType);
                }
                else
                    funcInstance = Expression.Constant(functionInfo.Function, funcType);

                return Expression.Call(
                    funcInstance,
                    funcType.GetMethod("Invoke", parameterTypes),
                    arguments);
            }
            else
            {
                throw new ArgumentException(string.Format("Unsupported operation \"{0}\".", operation.GetType().FullName), "operation");
            }
        }

        private Type GetFuncType(int numberOfParameters)
        {
            string funcTypeName;
            if (numberOfParameters < 9)
                funcTypeName = string.Format("System.Func`{0}", numberOfParameters + 1);
            else
                funcTypeName = string.Format("System.Func`{0}, {1}", numberOfParameters + 1, FuncAssemblyQualifiedName);
            Type funcType = Type.GetType(funcTypeName);

            Type[] typeArguments = new Type[numberOfParameters + 1];
            for (int i = 0; i < typeArguments.Length; i++)
                typeArguments[i] = typeof(double);

            return funcType.MakeGenericType(typeArguments);
        }

        private static class PrecompiledMethods
        {
            public static double GetVariableValueOrThrow(string variableName, FormulaContext context)
            {
                if (context.Variables.TryGetValue(variableName, out double result))
                    return result;
                else if (context.ConstantRegistry.IsConstantName(variableName))
                    return context.ConstantRegistry.GetConstantInfo(variableName).Value;
                else
                    throw new VariableNotDefinedException($"The variable \"{variableName}\" used is not defined.");
            }

            public static double CheckIfVariableExists(string variableName, FormulaContext context)
            {
                if (context.Variables.TryGetValue(variableName, out double result))
                    return 1.0;
                else if (context.ConstantRegistry.IsConstantName(variableName))
                    return 1.0;
                else
                    return 0.0;
            }
        }
    }
}
