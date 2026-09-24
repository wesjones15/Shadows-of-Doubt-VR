using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

class Program
{
    static void InspectDll(string dllPath, Action<MetadataReader> action)
    {
        using var stream = File.OpenRead(dllPath);
        using var peReader = new PEReader(stream);
        var mdReader = peReader.GetMetadataReader();
        action(mdReader);
    }

    static void DumpType(MetadataReader mdReader, string typeName, bool fieldsToo = true)
    {
        bool found = false;
        foreach (var typeHandle in mdReader.TypeDefinitions)
        {
            var typeDef = mdReader.GetTypeDefinition(typeHandle);
            if (mdReader.GetString(typeDef.Name) != typeName) continue;
            found = true;
            Console.WriteLine($"=== {mdReader.GetString(typeDef.Namespace)}.{typeName} ===");

            if (fieldsToo)
            {
                Console.WriteLine("-- Fields --");
                foreach (var fieldHandle in typeDef.GetFields())
                {
                    var field = mdReader.GetFieldDefinition(fieldHandle);
                    Console.WriteLine($"  {field.Attributes} {mdReader.GetString(field.Name)}");
                }
            }

            Console.WriteLine("-- Methods --");
            foreach (var methodHandle in typeDef.GetMethods())
            {
                var method = mdReader.GetMethodDefinition(methodHandle);
                Console.WriteLine($"  [{method.Attributes}] {mdReader.GetString(method.Name)}");
            }

            Console.WriteLine("-- Properties --");
            foreach (var propHandle in typeDef.GetProperties())
            {
                var prop = mdReader.GetPropertyDefinition(propHandle);
                Console.WriteLine($"  {mdReader.GetString(prop.Name)}");
            }
            break;
        }
        if (!found) Console.WriteLine($"{typeName} NOT FOUND");
        Console.WriteLine();
    }

    static void Main()
    {
        var hdrpDll = @"W:\SteamLibrary\steamapps\common\Shadows Of Doubt\BepInEx\interop\Unity.RenderPipelines.HighDefinition.Runtime.dll";

        InspectDll(hdrpDll, mdReader =>
        {
            DumpType(mdReader, "HDRenderPipelineGlobalSettings");
            DumpType(mdReader, "FrameSettingsRenderType", fieldsToo: true);
            DumpType(mdReader, "FrameSettings");
            DumpType(mdReader, "FrameSettingsField", fieldsToo: true);
        });

        Console.WriteLine("=== Searching ALL types in HDRP runtime for 'DefaultFrameSettings' in field/method/property names ===");
        InspectDll(hdrpDll, mdReader =>
        {
            foreach (var typeHandle in mdReader.TypeDefinitions)
            {
                var typeDef = mdReader.GetTypeDefinition(typeHandle);
                var typeName = mdReader.GetString(typeDef.Name);

                foreach (var fieldHandle in typeDef.GetFields())
                {
                    var field = mdReader.GetFieldDefinition(fieldHandle);
                    var fn = mdReader.GetString(field.Name);
                    if (fn.IndexOf("DefaultFrameSettings", StringComparison.OrdinalIgnoreCase) >= 0)
                        Console.WriteLine($"  FIELD {typeName}.{fn}  [{field.Attributes}]");
                }
                foreach (var methodHandle in typeDef.GetMethods())
                {
                    var method = mdReader.GetMethodDefinition(methodHandle);
                    var mn = mdReader.GetString(method.Name);
                    if (mn.IndexOf("DefaultFrameSettings", StringComparison.OrdinalIgnoreCase) >= 0)
                        Console.WriteLine($"  METHOD {typeName}.{mn}  [{method.Attributes}]");
                }
                foreach (var propHandle in typeDef.GetProperties())
                {
                    var prop = mdReader.GetPropertyDefinition(propHandle);
                    var pn = mdReader.GetString(prop.Name);
                    if (pn.IndexOf("DefaultFrameSettings", StringComparison.OrdinalIgnoreCase) >= 0)
                        Console.WriteLine($"  PROP {typeName}.{pn}");
                }
            }
        });
    }
}
