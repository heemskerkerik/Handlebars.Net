using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HandlebarsDotNet.Collections;
using HandlebarsDotNet.Compiler.Middlewares;
using HandlebarsDotNet.Compiler.Resolvers;
using HandlebarsDotNet.EqualityComparers;
using HandlebarsDotNet.Features;
using HandlebarsDotNet.Helpers;
using HandlebarsDotNet.IO;
using HandlebarsDotNet.ObjectDescriptors;
using HandlebarsDotNet.Runtime;

namespace HandlebarsDotNet
{
    internal class HandlebarsConfigurationAdapter : ICompiledHandlebarsConfiguration
    {
        public HandlebarsConfigurationAdapter(HandlebarsConfiguration configuration)
        {
            UnderlingConfiguration = configuration;

            AliasProviders = new ObservableList<IMemberAliasProvider>(configuration.AliasProviders);
            HelperResolvers = new ObservableList<IHelperResolver>(configuration.HelperResolvers);
            RegisteredTemplates = new ObservableIndex<string, HandlebarsTemplate<TextWriter, object, object>, StringEqualityComparer>(new StringEqualityComparer(StringComparison.OrdinalIgnoreCase), configuration.RegisteredTemplates);
            AliasProviders = new ObservableList<IMemberAliasProvider>(configuration.AliasProviders);
            FormatterProviders = new ObservableList<IFormatterProvider>
            {
                new DefaultFormatterProvider(),
                new CollectionFormatterProvider(),
                new ReadOnlyCollectionFormatterProvider(),
            }.AddMany(configuration.FormatterProviders);
            
            ObjectDescriptorProvider = CreateObjectDescriptorProvider();
            ExpressionMiddlewares = new ObservableList<IExpressionMiddleware>(configuration.CompileTimeConfiguration.ExpressionMiddleware)
            {
                new ClosureExpressionMiddleware(),
                new ExpressionOptimizerMiddleware()
            };

            FormatterProvider = new AggregatedFormatterProvider((ObservableList<IFormatterProvider>) FormatterProviders);                
            Features = UnderlingConfiguration.CompileTimeConfiguration.Features
                .Select(o => o.CreateFeature())
                .OrderBy(o => o.GetType().GetTypeInfo().GetCustomAttribute<FeatureOrderAttribute>()?.Order ?? 100)
                .ToList();
            
            Helpers = CreateHelpersSubscription(configuration.Helpers);
            BlockHelpers = CreateHelpersSubscription(configuration.BlockHelpers);
        }

        public HandlebarsConfiguration UnderlingConfiguration { get; }
        public IExpressionNameResolver ExpressionNameResolver => UnderlingConfiguration.ExpressionNameResolver;
        public ITextEncoder TextEncoder => UnderlingConfiguration.TextEncoder;
        public IFormatProvider FormatProvider => UnderlingConfiguration.FormatProvider;
        public ViewEngineFileSystem FileSystem => UnderlingConfiguration.FileSystem;
        public IAppendOnlyList<IFormatterProvider> FormatterProviders { get; }
        public bool ThrowOnUnresolvedBindingExpression => UnderlingConfiguration.ThrowOnUnresolvedBindingExpression;
        public IPartialTemplateResolver PartialTemplateResolver => UnderlingConfiguration.PartialTemplateResolver;
        public IMissingPartialTemplateHandler MissingPartialTemplateHandler => UnderlingConfiguration.MissingPartialTemplateHandler;
        public Compatibility Compatibility => UnderlingConfiguration.Compatibility;
        public IFormatterProvider FormatterProvider { get; }
        
        public bool NoEscape => UnderlingConfiguration.NoEscape;
        
        public IObjectDescriptorProvider ObjectDescriptorProvider { get; }
        public IAppendOnlyList<IExpressionMiddleware> ExpressionMiddlewares { get; }
        public IAppendOnlyList<IMemberAliasProvider> AliasProviders { get; }
        public IExpressionCompiler ExpressionCompiler { get; set; }
        public IReadOnlyList<IFeature> Features { get; }
        
        public IIndexed<PathInfoLight, Ref<IHelperDescriptor<HelperOptions>>> Helpers { get; }
        public IIndexed<PathInfoLight, Ref<IHelperDescriptor<BlockHelperOptions>>> BlockHelpers { get; }
        public IAppendOnlyList<IHelperResolver> HelperResolvers { get; }
        public IIndexed<string, HandlebarsTemplate<TextWriter, object, object>> RegisteredTemplates { get; }
        
        private ObservableIndex<PathInfoLight, Ref<IHelperDescriptor<TOptions>>, IEqualityComparer<PathInfoLight>> CreateHelpersSubscription<TOptions>(
            IIndexed<string, IHelperDescriptor<TOptions>> source) 
            where TOptions : struct, IHelperOptions
        {
            var equalityComparer = Compatibility.RelaxedHelperNaming ? PathInfoLight.PlainPathComparer : PathInfoLight.PlainPathWithPartsCountComparer;
            var existingHelpers = source.ToIndexed(
                o => (PathInfoLight) $"[{o.Key}]", 
                o => new Ref<IHelperDescriptor<TOptions>>(o.Value),
                equalityComparer
            );
            
            var target = new ObservableIndex<PathInfoLight, Ref<IHelperDescriptor<TOptions>>, IEqualityComparer<PathInfoLight>>(equalityComparer, existingHelpers);

            var helpersObserver = new ObserverBuilder<ObservableEvent<IHelperDescriptor<TOptions>>>()
                .OnEvent<
                    DictionaryAddedObservableEvent<string, IHelperDescriptor<TOptions>>,
                    ObservableIndex<PathInfoLight, Ref<IHelperDescriptor<TOptions>>, IEqualityComparer<PathInfoLight>>
                >(target,
                    (@event, state) =>
                    {
                        PathInfoLight key = $"[{@event.Key}]";
                        if (state.TryGetValue(key, out var @ref))
                        {
                            @ref.Value = @event.Value;
                            return;
                        }
                        
                        state.AddOrReplace(key, new Ref<IHelperDescriptor<TOptions>>(@event.Value));
                    })
                .Build();

            source.As<ObservableIndex<string, IHelperDescriptor<TOptions>, StringEqualityComparer>>()?.Subscribe(helpersObserver);

            return target;
        }
        
        private ObjectDescriptorFactory CreateObjectDescriptorProvider()
        {
            var objectDescriptorProvider = new ObjectDescriptorProvider(AliasProviders);
            var providers = new ObservableList<IObjectDescriptorProvider>
                {
                    objectDescriptorProvider,
                    new DynamicObjectDescriptor(),
                    new EnumerableObjectDescriptor(objectDescriptorProvider),
                    new DictionaryObjectDescriptor(),
                    new ReadOnlyGenericDictionaryObjectDescriptorProvider(),
                    new GenericDictionaryObjectDescriptorProvider(),
                    new ReadOnlyStringDictionaryObjectDescriptorProvider(),
                    new StringDictionaryObjectDescriptorProvider(),
                }
                .AddMany(UnderlingConfiguration.ObjectDescriptorProviders);

            return new ObjectDescriptorFactory(providers);
        }
    }
}