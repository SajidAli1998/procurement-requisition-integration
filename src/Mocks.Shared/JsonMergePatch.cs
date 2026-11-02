using System.Text.Json.Nodes;

namespace Mocks.Shared;

/// <summary>RFC 7386 JSON Merge Patch.</summary>
public static class JsonMergePatch
{
    public static void Apply(JsonObject target, JsonObject patch)
    {
        foreach (var (name, value) in patch)
        {
            if (value is null)
            {
                target.Remove(name);
            }
            else if (value is JsonObject patchObject && target[name] is JsonObject targetObject)
            {
                Apply(targetObject, patchObject);
            }
            else
            {
                target[name] = value.DeepClone();
            }
        }
    }
}
