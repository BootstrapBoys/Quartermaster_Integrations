using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;

namespace Quartermaster_Integrations
{
    /// <summary>
    /// The in-game Configuration Manager lists a category's settings by
    /// ConfigurationManagerAttributes.Order (descending), then alphabetically by name - which puts
    /// "Enabled" last in General despite it being bound first. The attributes type lives in that
    /// optional plugin, so it is located at runtime and filled in by reflection: with no Configuration
    /// Manager installed the tag is absent and nothing changes. Categories follow bind order, so
    /// 1. General / 2. Debug / 3. Visuals / 4. Patches need no tags.
    /// </summary>
    internal static class ConfigOrder
    {
        private static readonly Type AttributesType = FindAttributesType();

        /// <summary>A description whose entry sorts above untagged ones when Order is positive.</summary>
        public static ConfigDescription Description(string text, int order)
        {
            object tag = CreateTag(order);
            return tag == null ? new ConfigDescription(text) : new ConfigDescription(text, null, tag);
        }

        private static object CreateTag(int order)
        {
            if (AttributesType == null) return null;

            try
            {
                object tag = Activator.CreateInstance(AttributesType);
                AttributesType.GetField("Order")?.SetValue(tag, order);
                return tag;
            }
            catch
            {
                return null;
            }
        }

        private static Type FindAttributesType()
        {
            Type type = Type.GetType("ConfigurationManagerAttributes, ConfigurationManager", throwOnError: false);
            if (type != null) return type;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType("ConfigurationManagerAttributes", throwOnError: false);
                if (type != null) return type;
            }

            try
            {
                foreach (string dll in Directory.GetFiles(Paths.PluginPath, "ConfigurationManager.dll", SearchOption.AllDirectories))
                {
                    type = Assembly.LoadFrom(dll).GetType("ConfigurationManagerAttributes", throwOnError: false);
                    if (type != null) return type;
                }
            }
            catch
            {
                // Plugin folder unreadable or the assembly refuses to load - ordering is cosmetic.
            }

            return null;
        }
    }
}
