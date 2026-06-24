using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace OwnWand.Payload;

public class HookTarget
{
    public string Namespace { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string PatchType { get; set; } = "Prefix";
    public string Action { get; set; } = "skip";
    public string? Field { get; set; }
    public string? Value { get; set; }
}

public static class HookHandler
{
    private static readonly Harmony _harmony = new("com.ownwand.payload");
    private static readonly Dictionary<string, HookTarget> _registeredHooks = new();
    private static readonly Dictionary<string, bool> _featureStates = new();
    private static readonly Dictionary<string, float> _featureValues = new();
    private static readonly Dictionary<MethodBase, string> _methodToFeatureMap = new();

    public static void RegisterFeature(string featureId, HookTarget target, bool defaultEnabled, float defaultValue)
    {
        _registeredHooks[featureId] = target;
        _featureStates[featureId] = defaultEnabled;
        _featureValues[featureId] = defaultValue;

        if (defaultEnabled)
        {
            ApplyHook(featureId);
        }
    }

    public static void ToggleFeature(string featureId, bool enabled)
    {
        _featureStates[featureId] = enabled;
        if (enabled)
        {
            ApplyHook(featureId);
        }
    }

    public static void SetFeatureValue(string featureId, float value)
    {
        _featureValues[featureId] = value;
    }

    private static void ApplyHook(string featureId)
    {
        if (!_registeredHooks.TryGetValue(featureId, out var target)) return;

        try
        {
            var type = FindType(target.Namespace, target.ClassName);
            if (type == null) return;

            var method = type.GetMethod(target.MethodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (method == null) return;

            // Avoid double patching same method
            if (_methodToFeatureMap.ContainsKey(method)) return;
            _methodToFeatureMap[method] = featureId;

            MethodInfo patchMethod;
            bool isPrefix = target.PatchType.Equals("Prefix", StringComparison.OrdinalIgnoreCase);

            if (isPrefix)
            {
                if (method.ReturnType == typeof(void))
                    patchMethod = typeof(HookHandler).GetMethod(nameof(PrefixVoid), BindingFlags.Static | BindingFlags.NonPublic);
                else if (method.ReturnType == typeof(bool))
                    patchMethod = typeof(HookHandler).GetMethod(nameof(PrefixBool), BindingFlags.Static | BindingFlags.NonPublic);
                else
                    patchMethod = typeof(HookHandler).GetMethod(nameof(PrefixVoid), BindingFlags.Static | BindingFlags.NonPublic);

                _harmony.Patch(method, prefix: new HarmonyMethod(patchMethod));
            }
            else
            {
                if (method.ReturnType == typeof(void))
                    patchMethod = typeof(HookHandler).GetMethod(nameof(PostfixVoid), BindingFlags.Static | BindingFlags.NonPublic);
                else if (method.ReturnType == typeof(bool))
                    patchMethod = typeof(HookHandler).GetMethod(nameof(PostfixBool), BindingFlags.Static | BindingFlags.NonPublic);
                else
                    patchMethod = typeof(HookHandler).GetMethod(nameof(PostfixVoid), BindingFlags.Static | BindingFlags.NonPublic);

                _harmony.Patch(method, postfix: new HarmonyMethod(patchMethod));
            }
        }
        catch
        {
            // Log hook failure
        }
    }

    private static Type? FindType(string ns, string className)
    {
        var fullName = string.IsNullOrEmpty(ns) ? className : $"{ns}.{className}";
        var type = Type.GetType(fullName);
        if (type != null) return type;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(fullName);
            if (type != null) return type;

            type = assembly.GetType(className);
            if (type != null) return type;
        }

        return null;
    }

    // Generic Patch Handlers
    private static bool PrefixVoid(object __instance, MethodBase __originalMethod)
    {
        if (!_methodToFeatureMap.TryGetValue(__originalMethod, out var featureId)) return true;
        if (!_featureStates.TryGetValue(featureId, out var enabled) || !enabled) return true;

        var target = _registeredHooks[featureId];
        return HandleAction(__instance, target, featureId);
    }

    private static bool PrefixBool(object __instance, ref bool __result, MethodBase __originalMethod)
    {
        if (!_methodToFeatureMap.TryGetValue(__originalMethod, out var featureId)) return true;
        if (!_featureStates.TryGetValue(featureId, out var enabled) || !enabled) return true;

        var target = _registeredHooks[featureId];
        if (target.Action.Equals("override_return", StringComparison.OrdinalIgnoreCase))
        {
            if (bool.TryParse(target.Value, out bool val))
            {
                __result = val;
                return false; // Skip original
            }
        }

        return HandleAction(__instance, target, featureId);
    }

    private static void PostfixVoid(object __instance, MethodBase __originalMethod)
    {
        if (!_methodToFeatureMap.TryGetValue(__originalMethod, out var featureId)) return;
        if (!_featureStates.TryGetValue(featureId, out var enabled) || !enabled) return;

        var target = _registeredHooks[featureId];
        HandleAction(__instance, target, featureId);
    }

    private static void PostfixBool(object __instance, ref bool __result, MethodBase __originalMethod)
    {
        if (!_methodToFeatureMap.TryGetValue(__originalMethod, out var featureId)) return;
        if (!_featureStates.TryGetValue(featureId, out var enabled) || !enabled) return;

        var target = _registeredHooks[featureId];
        if (target.Action.Equals("override_return", StringComparison.OrdinalIgnoreCase))
        {
            if (bool.TryParse(target.Value, out bool val))
            {
                __result = val;
            }
        }
        else
        {
            HandleAction(__instance, target, featureId);
        }
    }

    private static bool HandleAction(object instance, HookTarget target, string featureId)
    {
        if (target.Action.Equals("skip", StringComparison.OrdinalIgnoreCase))
        {
            return false; // Skip original method
        }

        if (instance == null) return true;

        if (target.Action.Equals("set_field", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(target.Field))
        {
            var field = FindField(instance.GetType(), target.Field);
            if (field != null)
            {
                object valToSet = ConvertValue(target.Value, field.FieldType, instance, target.Field);
                field.SetValue(instance, valToSet);
            }
        }
        else if (target.Action.Equals("multiply_field", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(target.Field))
        {
            var field = FindField(instance.GetType(), target.Field);
            if (field != null)
            {
                _featureValues.TryGetValue(featureId, out float mult);
                if (mult <= 0f) mult = 1f;

                var currentVal = Convert.ToDouble(field.GetValue(instance));
                var newVal = currentVal * mult;
                field.SetValue(instance, Convert.ChangeType(newVal, field.FieldType));
            }
        }

        return true;
    }

    private static FieldInfo? FindField(Type type, string fieldName)
    {
        while (type != null)
        {
            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.IgnoreCase);
            if (field != null) return field;
            type = type.BaseType;
        }
        return null;
    }

    private static object ConvertValue(string? val, Type targetType, object instance, string fieldName)
    {
        if (val == null) return null;

        if (val.Equals("max", StringComparison.OrdinalIgnoreCase))
        {
            // Look for max field
            var maxField = FindField(instance.GetType(), "max" + fieldName) 
                           ?? FindField(instance.GetType(), "max_" + fieldName)
                           ?? FindField(instance.GetType(), "limit" + fieldName);
            if (maxField != null)
            {
                return maxField.GetValue(instance);
            }
            if (targetType == typeof(float)) return 100f;
            if (targetType == typeof(int)) return 100;
        }
        else if (val.Equals("slider", StringComparison.OrdinalIgnoreCase))
        {
            // Unused here but safety fallback
            return Convert.ChangeType(1f, targetType);
        }

        if (targetType == typeof(bool))
        {
            return bool.TryParse(val, out bool b) ? b : (object)true;
        }

        return Convert.ChangeType(val, targetType);
    }
}
