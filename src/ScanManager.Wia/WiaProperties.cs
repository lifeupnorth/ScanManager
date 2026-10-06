using System.Runtime.InteropServices;

namespace ScanManager.Wia;

/// <summary>
/// Helpers for WIA.Properties collections accessed through late binding. Late binding
/// avoids shipping a COM interop assembly; wiaaut.dll is part of every Windows install.
/// Parameters are typed <c>object</c> so calls bind statically and results stay typed.
/// </summary>
internal static class WiaProperties
{
    public static object? Find(object properties, int id)
    {
        foreach (dynamic p in (dynamic)properties)
        {
            if ((int)p.PropertyID == id) return p;
        }
        return null;
    }

    public static int? GetInt(object properties, int id)
    {
        dynamic? p = Find(properties, id);
        if (p is null) return null;
        try { return Convert.ToInt32((object)p.Value); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or COMException) { return null; }
    }

    public static string? GetString(object properties, int id)
    {
        dynamic? p = Find(properties, id);
        if (p is null) return null;
        try { return Convert.ToString((object)p.Value); }
        catch (COMException) { return null; }
    }

    /// <summary>Allowed values of a list-type property, or null if it isn't a list.</summary>
    public static List<int>? GetValidValues(object? property)
    {
        dynamic? p = property;
        if (p is null || (int)p.SubType != WiaConstants.SubTypeList) return null;
        var values = new List<int>();
        foreach (object v in p.SubTypeValues)
            values.Add(Convert.ToInt32(v));
        return values;
    }

    /// <summary>Allowed range of a range-type property, or null if it isn't a range.</summary>
    public static (int Min, int Max, int Step)? GetRange(object? property)
    {
        dynamic? p = property;
        if (p is null || (int)p.SubType != WiaConstants.SubTypeRange) return null;
        return ((int)p.SubTypeMin, (int)p.SubTypeMax, (int)p.SubTypeStep);
    }

    /// <summary>Sets a property, snapping the value to what the device allows. Returns false if unsupported.</summary>
    public static bool TrySet(object properties, int id, int value)
    {
        object? property = Find(properties, id);
        if (property is null || IsReadOnly(property)) return false;

        if (GetRange(property) is var (min, max, step))
        {
            value = Math.Clamp(value, min, max);
            if (step > 1) value = min + (value - min) / step * step;
        }
        else if (GetValidValues(property) is { Count: > 0 } values && !values.Contains(value))
        {
            value = values.MinBy(v => Math.Abs(v - value));
        }
        return TryAssign(property, value);
    }

    /// <summary>Sets a property only when the device lists the value as allowed.</summary>
    public static bool TrySetExact(object properties, int id, int value)
    {
        object? property = Find(properties, id);
        if (property is null || IsReadOnly(property)) return false;
        if (GetValidValues(property) is { } values && !values.Contains(value)) return false;
        return TryAssign(property, value);
    }

    private static bool IsReadOnly(object property) => (bool)((dynamic)property).IsReadOnly;

    private static bool TryAssign(object property, int value)
    {
        try
        {
            ((dynamic)property).Value = value;
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }
}
