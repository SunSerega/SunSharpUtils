using System;
using System.Collections.Concurrent;

namespace SunSharpUtils;

/// <summary>
/// Base class for models that can be converted multiple other model types, where all converted models need to be cached for performance reasons
/// <para/>
/// Thread-safe
/// </summary>
public abstract class ConvertibleWithCache
{

    private protected abstract class CachedConvertedModel;
    private protected sealed class CachedConvertedModel<T> : CachedConvertedModel
    {
        public required T Result { get; init; }
    }

    private protected interface IModelConverter;
    /// <summary>
    /// </summary>
    /// <typeparam name="TSelf">Back-reference to the derived converter type for <see cref="Singleton{TSelf}"/></typeparam>
    /// <typeparam name="TSource">Source model type to be converted</typeparam>
    /// <typeparam name="TResult">Result model type of the conversion</typeparam>
    public abstract class ModelConverter<TSelf, TSource, TResult> : Singleton<TSelf>, IModelConverter
        where TSelf : ModelConverter<TSelf, TSource, TResult>, new()
    {
        /// <summary>
        /// </summary>
        public abstract TResult Convert(TSource source);
    }

}

/// <inheritdoc cref="ConvertibleWithCache"/>
public abstract class ConvertibleWithCache<TSelf> : ConvertibleWithCache
    where TSelf : ConvertibleWithCache<TSelf>
{

    /// <summary>
    /// Called every time before <see cref="ConvertWithCache{TConverter, TResult}(ModelConverter{TConverter, TSelf, TResult})"/>
    /// </summary>
    protected virtual void SealBeforeConverting() { }

    private readonly ConcurrentDictionary<Type, CachedConvertedModel> cached_models = [];

    /// <summary>
    /// Converts the current model using the specified converter and caches the result for future use
    /// <para/>
    /// The converter must be the singleton instance of its type
    /// </summary>
    /// <typeparam name="TConverter"></typeparam>
    /// <typeparam name="TResult"></typeparam>
    /// <param name="converter"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public TResult ConvertWithCache<TConverter, TResult>(ModelConverter<TConverter, TSelf, TResult> converter)
        where TConverter : ModelConverter<TConverter, TSelf, TResult>, new()
    {
        if (converter != Singleton<TConverter>.Instance)
            throw new ArgumentException($"Converter must be the singleton instance of {typeof(TConverter).FullName}", nameof(converter));

        this.SealBeforeConverting();
        var cache_entry = (CachedConvertedModel<TResult>)this.cached_models.GetOrAdd(typeof(TConverter), _ => new CachedConvertedModel<TResult>
        {
            Result = converter.Convert((TSelf)this),
        });
        return cache_entry.Result;
    }

}
