using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;

namespace ComfyModelManager
{
    /// <summary>
    /// Plockar fram de modellvärden som en workflow faktiskt använder.
    /// - Noder med mode 2 (muted) och 4 (bypassed) ignoreras, liksom anteckningsnoder.
    /// - Ett widgetvärde som är kopplat till en ingång räknas bara bort när länken
    ///   bevisligen förser noden med ett värde. Annars behålls nodens sparade värde
    ///   (säkra sidan: en använd modell får aldrig bli oanvänd).
    /// - Subgrafer följs bara via aktiva instanser. Instansens promoted widget-värden
    ///   räknas, men bara för ingångar som används inuti subgrafen.
    /// Vid minsta tvekan returneras råtexten.
    /// </summary>
    public static class WorkflowFilter
    {
        private const int ModeNever = 2;
        private const int ModeBypass = 4;
        private const int MaxTextLength = 32 * 1024 * 1024;

        private sealed class InstanceInfo
        {
            public readonly HashSet<string> SuppliedInputs =
                new HashSet<string>(StringComparer.Ordinal);
        }

        public static string GetActiveText(string rawText)
        {
            if (string.IsNullOrEmpty(rawText)) return rawText;
            if (rawText.Length > MaxTextLength) return rawText;
            try
            {
                return GetActiveTextCore(rawText);
            }
            catch
            {
                return rawText;
            }
        }

        private static string GetActiveTextCore(string rawText)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 512;

            object root = serializer.DeserializeObject(rawText);
            Dictionary<string, object> rootMap = root as Dictionary<string, object>;
            if (rootMap == null) return rawText;

            object nodesValue;
            if (!rootMap.TryGetValue("nodes", out nodesValue)) return rawText;
            object[] nodes = nodesValue as object[];
            if (nodes == null) return rawText;

            // Äldre gruppnoder kan innehålla modeller som vi inte kan analysera säkert.
            if (HasLegacyGroupNodes(rootMap)) return rawText;

            Dictionary<string, Dictionary<string, object>> subgraphs = GetSubgraphs(rootMap);
            if (subgraphs == null) return rawText;

            // Fas 1: hitta alla aktiva subgraf-instanser och vilka ingångar de förser.
            Dictionary<string, List<InstanceInfo>> instances =
                new Dictionary<string, List<InstanceInfo>>(StringComparer.Ordinal);
            HashSet<string> collected = new HashSet<string>(StringComparer.Ordinal);
            CollectInstances(nodes, subgraphs, instances, collected);

            // Fas 2: bygg texten.
            StringBuilder activeText = new StringBuilder(rawText.Length);
            HashSet<string> emitted = new HashSet<string>(StringComparer.Ordinal);
            EmitNodes(
                nodes,
                subgraphs,
                instances,
                BuildLinkIndex(GetArray(rootMap, "links")),
                BuildNodeIndex(nodes),
                null,
                activeText,
                emitted);

            return activeText.ToString();
        }

        private static void CollectInstances(
            object[] nodes,
            Dictionary<string, Dictionary<string, object>> subgraphs,
            Dictionary<string, List<InstanceInfo>> instances,
            HashSet<string> collectedDefinitions)
        {
            foreach (object item in nodes)
            {
                Dictionary<string, object> node = item as Dictionary<string, object>;
                if (node == null) continue;

                int mode = GetInt(node, "mode");
                if (mode == ModeNever || mode == ModeBypass) continue;

                string type = GetString(node, "type");
                if (IsNoteType(type)) continue;

                Dictionary<string, object> definition;
                if (!string.IsNullOrEmpty(type) &&
                    subgraphs.TryGetValue(type, out definition) &&
                    GetArray(definition, "nodes") != null)
                {
                    List<InstanceInfo> list;
                    if (!instances.TryGetValue(type, out list))
                    {
                        list = new List<InstanceInfo>();
                        instances.Add(type, list);
                    }

                    InstanceInfo info = new InstanceInfo();
                    ReadSuppliedInputs(node, info.SuppliedInputs);
                    list.Add(info);

                    if (collectedDefinitions.Add(type))
                        CollectInstances(GetArray(definition, "nodes"), subgraphs, instances, collectedDefinitions);
                }
            }
        }

        private static void EmitNodes(
            object[] nodes,
            Dictionary<string, Dictionary<string, object>> subgraphs,
            Dictionary<string, List<InstanceInfo>> instances,
            Dictionary<int, Dictionary<string, object>> linkIndex,
            Dictionary<int, Dictionary<string, object>> nodeIndex,
            string containingDefinitionId,
            StringBuilder activeText,
            HashSet<string> emittedDefinitions)
        {
            foreach (object item in nodes)
            {
                Dictionary<string, object> node = item as Dictionary<string, object>;
                if (node == null) continue;

                int mode = GetInt(node, "mode");
                if (mode == ModeNever || mode == ModeBypass) continue;

                string type = GetString(node, "type");
                if (IsNoteType(type)) continue;

                Dictionary<string, object> definition;
                if (!string.IsNullOrEmpty(type) &&
                    subgraphs.TryGetValue(type, out definition) &&
                    GetArray(definition, "nodes") != null)
                {
                    EmitInstanceWidgets(node, definition, activeText);

                    if (emittedDefinitions.Add(type))
                    {
                        object[] definitionNodes = GetArray(definition, "nodes");
                        EmitNodes(
                            definitionNodes,
                            subgraphs,
                            instances,
                            BuildLinkIndex(GetArray(definition, "links")),
                            BuildNodeIndex(definitionNodes),
                            type,
                            activeText,
                            emittedDefinitions);
                    }
                    continue;
                }

                EmitNodeWidgets(node, linkIndex, nodeIndex, containingDefinitionId, subgraphs, instances, activeText);
            }
        }

        private static void EmitNodeWidgets(
            Dictionary<string, object> node,
            Dictionary<int, Dictionary<string, object>> linkIndex,
            Dictionary<int, Dictionary<string, object>> nodeIndex,
            string containingDefinitionId,
            Dictionary<string, Dictionary<string, object>> subgraphs,
            Dictionary<string, List<InstanceInfo>> instances,
            StringBuilder activeText)
        {
            HashSet<string> excludedNames;
            HashSet<int> excludedIndexes;
            CollectLinkedWidgets(
                node, linkIndex, nodeIndex, containingDefinitionId, subgraphs, instances,
                out excludedNames, out excludedIndexes);

            Dictionary<string, object> named = GetObject(node, "widgets_values_named");
            if (named != null)
            {
                foreach (KeyValuePair<string, object> pair in named)
                {
                    if (excludedNames.Contains(pair.Key)) continue;
                    AppendStringValues(pair.Value, activeText);
                }
                return;
            }

            AppendWidgetArray(node, activeText, excludedIndexes);
        }

        /// <summary>
        /// Tar värdena som en aktiv subgraf-instans skickar in. Endast ingångar som är
        /// kopplade inuti subgrafen räknas; externt länkade ingångar hoppas över.
        /// </summary>
        private static void EmitInstanceWidgets(
            Dictionary<string, object> instance,
            Dictionary<string, object> definition,
            StringBuilder activeText)
        {
            HashSet<string> linkedNames;
            HashSet<int> linkedIndexes;
            CollectLinkedWidgets(instance, null, null, null, null, null, out linkedNames, out linkedIndexes);

            HashSet<string> usedInputs = GetUsedSubgraphInputs(definition);
            Dictionary<string, object> named = GetObject(instance, "widgets_values_named");

            if (named != null)
            {
                int matched = 0;
                foreach (KeyValuePair<string, object> pair in named)
                {
                    if (linkedNames.Contains(pair.Key)) continue;
                    if (usedInputs.Count > 0 && !usedInputs.Contains(pair.Key)) continue;
                    AppendStringValues(pair.Value, activeText);
                    matched++;
                }

                // Om namnen inte matchar definitionen, var konservativ och ta alla.
                if (usedInputs.Count > 0 && matched == 0)
                {
                    foreach (KeyValuePair<string, object> pair in named)
                    {
                        if (linkedNames.Contains(pair.Key)) continue;
                        AppendStringValues(pair.Value, activeText);
                    }
                }
                return;
            }

            // Äldre format utan namngivna widgetvärden.
            AppendWidgetArray(instance, activeText, linkedIndexes);
        }

        /// <summary>
        /// Ett widgetvärde är bara "överskuggat" om länken har en aktiv källa.
        /// Vid subgraf-ingångar krävs dessutom att alla aktiva instanser faktiskt
        /// skickar in ett värde för ingången.
        /// </summary>
        private static void CollectLinkedWidgets(
            Dictionary<string, object> node,
            Dictionary<int, Dictionary<string, object>> linkIndex,
            Dictionary<int, Dictionary<string, object>> nodeIndex,
            string containingDefinitionId,
            Dictionary<string, Dictionary<string, object>> subgraphs,
            Dictionary<string, List<InstanceInfo>> instances,
            out HashSet<string> linkedNames,
            out HashSet<int> linkedIndexes)
        {
            linkedNames = new HashSet<string>(StringComparer.Ordinal);
            linkedIndexes = new HashSet<int>();

            object[] inputs = GetArray(node, "inputs");
            if (inputs == null) return;

            int widgetIndex = 0;
            foreach (object item in inputs)
            {
                Dictionary<string, object> input = item as Dictionary<string, object>;
                if (input == null) continue;

                Dictionary<string, object> widget = GetObject(input, "widget");
                if (widget == null) continue;

                bool linked = IsLinkSupplied(
                    input, linkIndex, nodeIndex, containingDefinitionId, subgraphs, instances);

                if (linked)
                {
                    string name = GetString(widget, "name");
                    if (!string.IsNullOrEmpty(name)) linkedNames.Add(name);
                    linkedIndexes.Add(widgetIndex);
                }
                widgetIndex++;
            }
        }

        private static bool IsLinkSupplied(
            Dictionary<string, object> input,
            Dictionary<int, Dictionary<string, object>> linkIndex,
            Dictionary<int, Dictionary<string, object>> nodeIndex,
            string containingDefinitionId,
            Dictionary<string, Dictionary<string, object>> subgraphs,
            Dictionary<string, List<InstanceInfo>> instances)
        {
            if (linkIndex == null) return false;

            object linkValue;
            if (!input.TryGetValue("link", out linkValue) || linkValue == null) return false;

            int linkId;
            try { linkId = Convert.ToInt32(linkValue, CultureInfo.InvariantCulture); }
            catch { return false; }

            Dictionary<string, object> link;
            if (!linkIndex.TryGetValue(linkId, out link)) return false;

            int originId = GetInt(link, "origin_id");
            if (originId >= 0)
            {
                if (nodeIndex == null) return false;
                Dictionary<string, object> source;
                if (!nodeIndex.TryGetValue(originId, out source)) return false;

                int sourceMode = GetInt(source, "mode");
                return sourceMode != ModeNever && sourceMode != ModeBypass;
            }

            // Subgraf-ingång: värdet måste komma från en aktiv instans.
            if (string.IsNullOrEmpty(containingDefinitionId) || subgraphs == null) return false;

            Dictionary<string, object> definition;
            if (!subgraphs.TryGetValue(containingDefinitionId, out definition)) return false;

            string inputName = FindSubgraphInputName(definition, linkId);
            if (string.IsNullOrEmpty(inputName)) return false;

            return AllInstancesSupply(instances, containingDefinitionId, inputName);
        }

        private static string FindSubgraphInputName(Dictionary<string, object> definition, int linkId)
        {
            object[] inputs = GetArray(definition, "inputs");
            if (inputs == null) return null;

            foreach (object item in inputs)
            {
                Dictionary<string, object> input = item as Dictionary<string, object>;
                if (input == null) continue;

                object[] linkIds = GetArray(input, "linkIds");
                if (linkIds == null) continue;

                for (int i = 0; i < linkIds.Length; i++)
                {
                    int candidate;
                    try { candidate = Convert.ToInt32(linkIds[i], CultureInfo.InvariantCulture); }
                    catch { continue; }
                    if (candidate == linkId) return GetString(input, "name");
                }
            }
            return null;
        }

        private static bool AllInstancesSupply(
            Dictionary<string, List<InstanceInfo>> instances,
            string definitionId,
            string inputName)
        {
            if (instances == null) return false;
            List<InstanceInfo> list;
            if (!instances.TryGetValue(definitionId, out list) || list.Count == 0) return false;

            for (int i = 0; i < list.Count; i++)
                if (!list[i].SuppliedInputs.Contains(inputName)) return false;

            return true;
        }

        private static void ReadSuppliedInputs(Dictionary<string, object> instance, HashSet<string> supplied)
        {
            Dictionary<string, object> named = GetObject(instance, "widgets_values_named");
            if (named == null) return;

            foreach (KeyValuePair<string, object> pair in named)
            {
                if (pair.Value == null) continue;
                string text = pair.Value as string;
                if (text == null || text.Length > 0) supplied.Add(pair.Key);
            }
        }

        private static HashSet<string> GetUsedSubgraphInputs(Dictionary<string, object> definition)
        {
            HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
            object[] inputs = GetArray(definition, "inputs");
            if (inputs == null) return used;

            foreach (object item in inputs)
            {
                Dictionary<string, object> input = item as Dictionary<string, object>;
                if (input == null) continue;

                string name = GetString(input, "name");
                object[] linkIds = GetArray(input, "linkIds");
                if (!string.IsNullOrEmpty(name) && linkIds != null && linkIds.Length > 0)
                    used.Add(name);
            }
            return used;
        }

        private static void AppendWidgetArray(Dictionary<string, object> node, StringBuilder activeText, HashSet<int> excludedIndexes)
        {
            object[] values = GetArray(node, "widgets_values");
            if (values == null) return;

            for (int i = 0; i < values.Length; i++)
            {
                if (excludedIndexes != null && excludedIndexes.Contains(i)) continue;
                AppendStringValues(values[i], activeText);
            }
        }

        private static void AppendStringValues(object value, StringBuilder activeText)
        {
            if (value == null) return;

            string text = value as string;
            if (text != null)
            {
                if (text.Length > 0) activeText.Append(text).Append('\n');
                return;
            }

            object[] array = value as object[];
            if (array != null)
            {
                for (int i = 0; i < array.Length; i++) AppendStringValues(array[i], activeText);
                return;
            }

            Dictionary<string, object> map = value as Dictionary<string, object>;
            if (map != null)
            {
                foreach (object item in map.Values) AppendStringValues(item, activeText);
            }
        }

        private static bool HasLegacyGroupNodes(Dictionary<string, object> root)
        {
            Dictionary<string, object> extra = GetObject(root, "extra");
            if (extra == null) return false;

            Dictionary<string, object> groups = GetObject(extra, "groupNodes");
            return groups != null && groups.Count > 0;
        }

        private static Dictionary<string, Dictionary<string, object>> GetSubgraphs(Dictionary<string, object> root)
        {
            Dictionary<string, Dictionary<string, object>> result =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);

            Dictionary<string, object> definitions = GetObject(root, "definitions");
            if (definitions == null) return result;

            object subgraphsValue;
            if (!definitions.TryGetValue("subgraphs", out subgraphsValue)) return result;
            if (subgraphsValue == null) return result;

            object[] subgraphs = subgraphsValue as object[];
            if (subgraphs == null) return null;

            foreach (object item in subgraphs)
            {
                Dictionary<string, object> definition = item as Dictionary<string, object>;
                if (definition == null) continue;

                string id = GetString(definition, "id");
                if (!string.IsNullOrEmpty(id)) result[id] = definition;
            }

            return result;
        }

        private static Dictionary<int, Dictionary<string, object>> BuildLinkIndex(object[] links)
        {
            Dictionary<int, Dictionary<string, object>> result = new Dictionary<int, Dictionary<string, object>>();
            if (links == null) return result;

            foreach (object item in links)
            {
                Dictionary<string, object> link = item as Dictionary<string, object>;
                if (link == null) continue;

                int id = GetInt(link, "id");
                if (id != 0) result[id] = link;
            }
            return result;
        }

        private static Dictionary<int, Dictionary<string, object>> BuildNodeIndex(object[] nodes)
        {
            Dictionary<int, Dictionary<string, object>> result = new Dictionary<int, Dictionary<string, object>>();
            if (nodes == null) return result;

            foreach (object item in nodes)
            {
                Dictionary<string, object> node = item as Dictionary<string, object>;
                if (node == null) continue;

                int id = GetInt(node, "id");
                if (id != 0) result[id] = node;
            }
            return result;
        }

        private static bool IsNoteType(string type)
        {
            return string.Equals(type, "Note", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "MarkdownNote", StringComparison.OrdinalIgnoreCase);
        }

        private static object[] GetArray(Dictionary<string, object> map, string key)
        {
            object value;
            if (!map.TryGetValue(key, out value)) return null;
            return value as object[];
        }

        private static Dictionary<string, object> GetObject(Dictionary<string, object> map, string key)
        {
            object value;
            if (!map.TryGetValue(key, out value)) return null;
            return value as Dictionary<string, object>;
        }

        private static string GetString(Dictionary<string, object> map, string key)
        {
            object value;
            if (!map.TryGetValue(key, out value)) return null;
            return value as string;
        }

        private static int GetInt(Dictionary<string, object> map, string key)
        {
            object value;
            if (!map.TryGetValue(key, out value) || value == null) return 0;
            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }
    }
}
