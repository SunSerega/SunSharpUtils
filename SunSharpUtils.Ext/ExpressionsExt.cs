using System;
using System.Linq.Expressions;

namespace SunSharpUtils.Ext.Expressions;

/// <summary>
/// Utils for creating <see cref="InvocationExpression"/> from lambda
/// </summary>
public static class ExpressionInvokeHelper
{
    /// <summary>
    /// </summary>
    public static InvocationExpression Wrap(Expression<Action> e_func) => Expression.Invoke(e_func);
    /// <summary>
    /// </summary>
    public static InvocationExpression Wrap<TRes>(Expression<Func<TRes>> e_func) => Expression.Invoke(e_func);
}

/// <inheritdoc cref="ExpressionInvokeHelper"/>
public static class ExpressionInvokeHelper<TInp>
{
    /// <inheritdoc cref="ExpressionInvokeHelper.Wrap(Expression{Action})"/>
    public static InvocationExpression Wrap(Expression<Action<TInp>> e_func, Expression e_arg) => Expression.Invoke(e_func, e_arg);
    /// <inheritdoc cref="ExpressionInvokeHelper.Wrap{TRes}(Expression{Func{TRes}})"/>
    public static InvocationExpression Wrap<TRes>(Expression<Func<TInp, TRes>> e_func, Expression e_arg) => Expression.Invoke(e_func, e_arg);
}

/// <inheritdoc cref="ExpressionInvokeHelper"/>
public static class ExpressionInvokeHelper<TInp1, TInp2>
{
    /// <inheritdoc cref="ExpressionInvokeHelper.Wrap(Expression{Action})"/>
    public static InvocationExpression Wrap(Expression<Action<TInp1, TInp2>> e_func, Expression e_arg1, Expression e_arg2) => Expression.Invoke(e_func, e_arg1, e_arg2);
    /// <inheritdoc cref="ExpressionInvokeHelper.Wrap{TRes}(Expression{Func{TRes}})"/>
    public static InvocationExpression Wrap<TRes>(Expression<Func<TInp1, TInp2, TRes>> e_func, Expression e_arg1, Expression e_arg2) => Expression.Invoke(e_func, e_arg1, e_arg2);
}

//public static class ExpressionsExt
//{

//}
