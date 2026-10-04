using System;
using System.Collections.Generic;
using Amplitude;
using Amplitude.Framework.Simulation;
using Amplitude.Framework.Simulation.Description;
using Amplitude.Mercury.Data.Simulation;
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Simulation;
using DatatableElementReference = Amplitude.Framework.DatatableElementReference;
using Descriptor = Amplitude.Framework.Simulation.Description.Descriptor;

namespace PopulationPlanner
{
    // Reads jobs and the job effects of populations from the simulation (sandbox thread only, from StateCapture).
    internal static class JobCapture
    {
        private sealed class TypeInfo
        {
            public readonly List<JobEffect> Effects = new List<JobEffect>();
            public readonly HashSet<string> Descriptors = new HashSet<string>(StringComparer.Ordinal);
            public bool FixedJob;
        }

        // Static game data: parsed once per population type.
        private static readonly Dictionary<string, TypeInfo> Types = new Dictionary<string, TypeInfo>(StringComparer.Ordinal);
        private static readonly Dictionary<string, StaticString> TagNames = new Dictionary<string, StaticString>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReportedUnsupported = new HashSet<string>(StringComparer.Ordinal);

        // Fills the state's job effects for these population types; returns the job descriptors they check.
        public static HashSet<string> ReadTypes(GameState state, IEnumerable<PopulationDefinition> definitions)
        {
            var tags = new HashSet<string>(StringComparer.Ordinal);
            foreach (PopulationDefinition definition in definitions)
            {
                string name = definition.Name.ToString();
                if (!Types.TryGetValue(name, out TypeInfo info))
                {
                    info = Parse(definition);
                    Types[name] = info;
                }
                state.JobEffects[name] = info.Effects;
                state.TypeDescriptors[name] = info.Descriptors;
                if (info.FixedJob)
                {
                    state.FixedJobTypes.Add(name);
                }
                foreach (JobEffect effect in info.Effects)
                {
                    tags.UnionWith(effect.RequiredTags);
                    tags.UnionWith(effect.ForbiddenTags);
                }
            }
            return tags;
        }

        public static JobState ReadJobs(Settlement settlement, ICollection<string> tags)
        {
            var jobs = new JobState { CanReassign = (settlement.CityFlags & CityFlags.UnderSubjugation) == 0 };
            FimsInfo weights = settlement.PopulationAssignementPonderation;
            jobs.Weights = new[] { (float)weights.Food, (float)weights.Industry, (float)weights.Money, (float)weights.Science, (float)weights.Influence, (float)weights.Approval };
            bool anyWeight = false;
            foreach (float weight in jobs.Weights)
            {
                anyWeight |= weight != 0f;
            }
            if (!anyWeight)
            {
                jobs.Weights = new[] { 1f, 1f, 1f, 1f, 1f, 1f };
            }
            ReferenceCollection<PopulationCategory> categories = settlement.ActivePopulationCategories;
            for (int i = 0; categories != null && i < categories.Count; i++)
            {
                PopulationCategory category = categories[i];
                if (ReferenceEquals(category, null) || ReferenceEquals(category.PopulationCategoryDefinition, null)
                    || category.PopulationCategoryDefinition.Name == DepartmentOfTheInterior.HomelessCategoryName)
                {
                    continue;
                }
                var job = new JobCategory
                {
                    Guid = category.GUID,
                    Name = category.PopulationCategoryDefinition.Name.ToString(),
                    Slots = (int)category.PopulationSlotCount.Value,
                };
                job.Base[Yield.Food] = (float)category.FoodPerPopulation.Value;
                job.Base[Yield.Industry] = (float)category.IndustryPerPopulation.Value;
                job.Base[Yield.Money] = (float)category.MoneyPerPopulation.Value;
                job.Base[Yield.Science] = (float)category.SciencePerPopulation.Value;
                job.Base[Yield.Influence] = (float)category.InfluencePerPopulation.Value;
                job.Base[Yield.Approval] = (float)category.ApprovalPerPopulation.Value;
                foreach (string tag in tags)
                {
                    if (category.ContainsDescriptor(TagName(tag)))
                    {
                        job.Tags.Add(tag);
                    }
                }
                for (int j = 0; j < category.Populations.Count; j++)
                {
                    Population population = category.Populations[j];
                    if (ReferenceEquals(population, null) || ReferenceEquals(population.PopulationDefinition, null))
                    {
                        continue;
                    }
                    job.Pops.Add(new JobPop(population.GUID, population.PopulationDefinition.Name.ToString(), (float)population.GetFIMSScore()));
                }
                jobs.Categories.Add(job);
            }
            return jobs;
        }

        private static StaticString TagName(string tag)
        {
            if (!TagNames.TryGetValue(tag, out StaticString name))
            {
                name = new StaticString(tag);
                TagNames[tag] = name;
            }
            return name;
        }

        private static TypeInfo Parse(PopulationDefinition definition)
        {
            var info = new TypeInfo();
            string type = definition.Name.ToString();
            ParseAll(type, definition.Descriptors, info);
            ParseAll(type, definition.WorkerDescriptors, info);
            return info;
        }

        private static void ParseAll(string type, DatatableElementReference[] references, TypeInfo info)
        {
            for (int i = 0; references != null && i < references.Length; i++)
            {
                string name = references[i].ElementName.ToString();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }
                info.Descriptors.Add(name);
                Descriptor descriptor = references[i].GetDatatableElement<Descriptor>();
                if (ReferenceEquals(descriptor, null) || descriptor.Effects == null)
                {
                    continue;
                }
                foreach (Effect effect in descriptor.Effects)
                {
                    ParseEffect(type, name, effect, info);
                }
            }
        }

        // Keeps the effects of a population that change one of the scored yields: those tied to its job (path starting
        // at its job), and its own flat ones (empty path), which don't change with the job but count when choosing what
        // to grow, e.g. for approval.
        private static void ParseEffect(string type, string descriptor, Effect effect, TypeInfo info)
        {
            string[] path = effect.Path.PropertyToFollow ?? new string[0];
            if ((path.Length > 0 && path[0] != "PopulationCategory") || effect.PropertyEffects == null)
            {
                return;
            }
            var required = new List<string>();
            var forbidden = new List<string>();
            string coworker = null;
            Validation[] validations = effect.Path.Validations;
            for (int i = 0; validations != null && i < validations.Length; i++)
            {
                Validation validation = validations[i];
                if (StaticString.IsNullOrEmpty(validation.ElementName))
                {
                    validation.OnEnable();
                }
                string tag = validation.ElementName.ToString();
                if (path.Length == 0)
                {
                    Unsupported(type, descriptor, "a condition on the population itself");
                    return;
                }
                if (validation.PathIndex == 0)
                {
                    (validation.Inverted ? forbidden : required).Add(tag);
                }
                else if (validation.PathIndex == 1 && path.Length > 1 && path[1] == "Populations" && !validation.Inverted && coworker == null)
                {
                    coworker = tag;
                }
                else
                {
                    Unsupported(type, descriptor, "a condition on " + string.Join(".", path));
                    return;
                }
            }
            foreach (PropertyEffect propertyEffect in effect.PropertyEffects)
            {
                if (propertyEffect == null)
                {
                    continue;
                }
                if (propertyEffect.TargetProperty == "PopulationSlotCount")
                {
                    // Moving this population would change how many fit in its job.
                    info.FixedJob = true;
                    continue;
                }
                int field = Yield.FromProperty(propertyEffect.TargetProperty);
                if (field < 0)
                {
                    continue;
                }
                float sign;
                if (propertyEffect.ToTargetOperation == Operation.Add)
                {
                    sign = 1f;
                }
                else if (propertyEffect.ToTargetOperation == Operation.Sub)
                {
                    sign = -1f;
                }
                else
                {
                    Unsupported(type, descriptor, propertyEffect.ToTargetOperation + " on " + propertyEffect.TargetProperty);
                    continue;
                }
                Operation[] ops = propertyEffect.RpnOperationStack ?? new Operation[0];
                FixedPoint[] constants = propertyEffect.ConstantStack ?? new FixedPoint[0];
                var jobEffect = new JobEffect
                {
                    Field = field,
                    Sign = sign,
                    RequiredTags = required.ToArray(),
                    ForbiddenTags = forbidden.ToArray(),
                    PerCoworkerDescriptor = coworker,
                    Ops = Array.ConvertAll(ops, op => (int)op),
                    Constants = Array.ConvertAll(constants, value => (float)value),
                    Properties = propertyEffect.PropertyLocalName ?? new string[0],
                };
                if (float.IsNaN(jobEffect.Evaluate(1)))
                {
                    Unsupported(type, descriptor, "its formula for " + propertyEffect.TargetProperty);
                    continue;
                }
                info.Effects.Add(jobEffect);
            }
        }

        private static void Unsupported(string type, string descriptor, string what)
        {
            if (ReportedUnsupported.Add(type + "|" + descriptor + "|" + what))
            {
                Plugin.Log.LogInfo($"Job effect of {type} ({descriptor}) not counted: the mod doesn't read {what}.");
            }
        }
    }
}
