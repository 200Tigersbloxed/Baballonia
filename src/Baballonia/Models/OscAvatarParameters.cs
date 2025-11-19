using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using VRCFaceTracking.Core.Contracts;

namespace Baballonia.Models;

public class OscAvatarParameters
{
    public string DESCRIPTION { get; set; }
    public string FULL_PATH { get; set; }
    public int ACCESS { get; set; }

    public string GetAvatarID()
    {
        bool noAvatarID = !CONTENTS.ContainsKey("avatar") || CONTENTS["avatar"].CONTENTS == null ||
                                  !CONTENTS["avatar"].CONTENTS!.ContainsKey("change") ||
                                  CONTENTS["avatar"].CONTENTS!["change"] == null;
        if (noAvatarID) return string.Empty;
        return CONTENTS["avatar"].CONTENTS!["change"].VALUE[0].ToString();
    }

    public Dictionary<string, OscAvatarContents> CONTENTS { get; set; }

    public OscAvatarContents[] GetParameters()
    {
        bool noAvatarParameters = !CONTENTS.ContainsKey("avatar") || CONTENTS["avatar"].CONTENTS == null ||
                                  !CONTENTS["avatar"].CONTENTS!.ContainsKey("parameters") ||
                                  CONTENTS["avatar"].CONTENTS!["parameters"] == null;
        if (noAvatarParameters) throw new InvalidOperationException("JSON file is missing required fields");
        // Get all of the parameters
        OscAvatarContents[] parameters = GetNestedContent(CONTENTS["avatar"].CONTENTS!["parameters"].CONTENTS);
        return parameters;
    }

    private OscAvatarContents[] GetNestedContent(Dictionary<string, OscAvatarContents> content)
    {
        List<OscAvatarContents> leaves = new List<OscAvatarContents>();
        foreach (KeyValuePair<string, OscAvatarContents> kvp in content)
        {
            OscAvatarContents node = kvp.Value;
            if(node.CONTENTS != null && node.CONTENTS.Count > 0)
                leaves.AddRange(GetNestedContent(node.CONTENTS));
            else
                leaves.Add(node);
        }
        return leaves.ToArray();
    }
}

public class OscAvatarContents : IParameterDefinition
{
    // Not null for nested contents (i.e. Avatar Parameters)
    public Dictionary<string, OscAvatarContents>? CONTENTS { get; set; }
    public string? DESCRIPTION { get; set; }
    public string? FULL_PATH { get; set; }
    public int? ACCESS { get; set; }
    public string? TYPE { get; set; }
    /*
     * Each entry could be a bool, int, or float. Check the TYPE property.
     * T - bool
     * f - float
     * i - integer
     * s - string
     * fn - f*n vectors (usually used for tracking and VALUE is more than likely null)
     */
    public object?[]? VALUE { get; set; }

    [JsonIgnore] public string Address => FULL_PATH?.Replace("/avatar/parameters/", String.Empty) ?? String.Empty;

    [JsonIgnore]
    public string Name =>
        FULL_PATH?.Replace("/avatar/parameters/", String.Empty).TrimEnd('/').Split('/').LastOrDefault() ?? String.Empty;

    [JsonIgnore]
    public Type Type
    {
        get
        {
            if (TYPE == null) return typeof(object);
            switch (TYPE)
            {
                case "T":
                    return typeof(bool);
                case "f":
                    return typeof(float);
                case "i":
                    return typeof(int);
            }
            return typeof(object);
        }
    }
}
