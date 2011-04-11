﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Web.Mvc;
using Autofac.Core;
using Orchard.ContentManagement;
using Orchard.ContentManagement.Records;
using Orchard.Environment.Configuration;
using Orchard.Environment.Descriptor.Models;
using Orchard.Environment.Extensions;
using Orchard.Environment.Extensions.Models;
using Orchard.Environment.ShellBuilders.Models;

namespace Orchard.Environment.ShellBuilders {
    /// <summary>
    /// Service at the host level to transform the cachable descriptor into the loadable blueprint.
    /// </summary>
    public interface ICompositionStrategy {
        /// <summary>
        /// Using information from the IExtensionManager, transforms and populates all of the
        /// blueprint model the shell builders will need to correctly initialize a tenant IoC container.
        /// </summary>
        ShellBlueprint Compose(ShellSettings settings, ShellDescriptor descriptor);
    }

    public class CompositionStrategy : ICompositionStrategy {
        private readonly IExtensionManager _extensionManager;

        public CompositionStrategy(IExtensionManager extensionManager) {
            _extensionManager = extensionManager;
        }

        public ShellBlueprint Compose(ShellSettings settings, ShellDescriptor descriptor) {
            var enabledFeatures = _extensionManager.EnabledFeatures(descriptor);
            var features = _extensionManager.LoadFeatures(enabledFeatures);

            if (descriptor.Features.Any(feature => feature.Name == "Orchard.Framework"))
                features = features.Concat(BuiltinFeatures());

            var modules = BuildBlueprint(features, IsModule, BuildModule);
            var dependencies = BuildBlueprint(features, IsDependency, (t, f) => BuildDependency(t, f, descriptor));
            var controllers = BuildBlueprint(features, IsController, BuildController);
            var records = BuildBlueprint(features, IsRecord, (t, f) => BuildRecord(t, f, settings));

            return new ShellBlueprint {
                Settings = settings,
                Descriptor = descriptor,
                Dependencies = dependencies.Concat(modules).ToArray(),
                Controllers = controllers,
                Records = records,
            };
        }

        private static IEnumerable<Feature> BuiltinFeatures() {
            yield return new Feature {
                Descriptor = new FeatureDescriptor {
                    Id = "Orchard.Framework",
                    Extension = new ExtensionDescriptor {
                        Id = "Orchard.Framework"
                    }
                },
                ExportedTypes =
                    typeof(OrchardStarter).Assembly.GetExportedTypes()
                    .Where(t => t.IsClass && !t.IsAbstract)
                    .Except(new[] { typeof(DefaultOrchardHost) })
                    .ToArray()
            };
        }

        private static IEnumerable<T> BuildBlueprint<T>(
            IEnumerable<Feature> features,
            Func<Type, bool> predicate,
            Func<Type, Feature, T> selector) {
            HashSet<string> excludedTypes = new HashSet<string>();

            // Identify replaced types
            foreach(Feature feature in features) {
                foreach (Type type in feature.ExportedTypes) {
                    foreach (OrchardSuppressDependencyAttribute replacedType in type.GetCustomAttributes(typeof(OrchardSuppressDependencyAttribute), false)) {
                        excludedTypes.Add(replacedType.FullName);
                    }
                }
            }

            // Load types excluding the replaced types
            return features.SelectMany(
                feature => feature.ExportedTypes
                               .Where(predicate)
                               .Where(type => !excludedTypes.Contains(type.FullName))
                               .Select(type => selector(type, feature)))
                .ToArray();
        }

        private static bool IsModule(Type type) {
            return typeof(IModule).IsAssignableFrom(type);
        }

        private static DependencyBlueprint BuildModule(Type type, Feature feature) {
            return new DependencyBlueprint { Type = type, Feature = feature, Parameters = Enumerable.Empty<ShellParameter>() };
        }

        private static bool IsDependency(Type type) {
            return typeof(IDependency).IsAssignableFrom(type);
        }

        private static DependencyBlueprint BuildDependency(Type type, Feature feature, ShellDescriptor descriptor) {
            return new DependencyBlueprint {
                Type = type,
                Feature = feature,
                Parameters = descriptor.Parameters.Where(x => x.Component == type.FullName).ToArray()
            };
        }

        private static bool IsController(Type type) {
            return typeof(IController).IsAssignableFrom(type);
        }

        private static ControllerBlueprint BuildController(Type type, Feature feature) {
            var areaName = feature.Descriptor.Extension.Id;

            var controllerName = type.Name;
            if (controllerName.EndsWith("Controller"))
                controllerName = controllerName.Substring(0, controllerName.Length - "Controller".Length);

            return new ControllerBlueprint {
                Type = type,
                Feature = feature,
                AreaName = areaName,
                ControllerName = controllerName,
            };
        }

        private static bool IsRecord(Type type) {
            return ((type.Namespace ?? "").EndsWith(".Models") || (type.Namespace ?? "").EndsWith(".Records")) &&
                   type.GetProperty("Id") != null &&
                   (type.GetProperty("Id").GetAccessors() ?? Enumerable.Empty<MethodInfo>()).All(x => x.IsVirtual) &&
                   !type.IsSealed &&
                   !type.IsAbstract &&
                   (!typeof(IContent).IsAssignableFrom(type) || typeof(ContentPartRecord).IsAssignableFrom(type));
        }

        private static RecordBlueprint BuildRecord(Type type, Feature feature, ShellSettings settings) {
            var extensionDescriptor = feature.Descriptor.Extension;
            var extensionName = extensionDescriptor.Id.Replace('.', '_');

            var dataTablePrefix = "";
            if (!string.IsNullOrEmpty(settings.DataTablePrefix))
                dataTablePrefix = settings.DataTablePrefix + "_";

            return new RecordBlueprint {
                Type = type,
                Feature = feature,
                TableName = dataTablePrefix + extensionName + '_' + type.Name,
            };
        }
    }
}