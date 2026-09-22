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

    static void Main()
    {
        var asmCsharp = @"E:\SteamLibrary\steamapps\common\Shadows of Doubt\BepInEx\interop\Assembly-CSharp.dll";
        var fmodDll   = @"E:\SteamLibrary\steamapps\common\Shadows of Doubt\BepInEx\interop\FMODUnity.dll";

        // ==================== TASK 1: Find class with masterVolumeScale ====================
        Console.WriteLine("=== TASK 1: Class with masterVolumeScale ===");
        InspectDll(asmCsharp, mdReader =>
        {
            string? targetClass = null;
            string? targetNamespace = null;
            TypeDefinitionHandle targetHandle = default;

            foreach (var typeHandle in mdReader.TypeDefinitions)
            {
                var typeDef = mdReader.GetTypeDefinition(typeHandle);
                foreach (var methodHandle in typeDef.GetMethods())
                {
                    var method = mdReader.GetMethodDefinition(methodHandle);
                    var methodName = mdReader.GetString(method.Name);
                    if (methodName == "get_masterVolumeScale" || methodName == "set_masterVolumeScale")
                    {
                        targetClass = mdReader.GetString(typeDef.Name);
                        targetNamespace = mdReader.GetString(typeDef.Namespace);
                        targetHandle = typeHandle;
                        break;
                    }
                }
                if (targetClass != null) break;
            }

            if (targetClass == null) { Console.WriteLine("NOT FOUND"); return; }

            Console.WriteLine($"Class: {targetNamespace}.{targetClass}");
            Console.WriteLine("All methods:");
            var typeDef2 = mdReader.GetTypeDefinition(targetHandle);
            foreach (var methodHandle in typeDef2.GetMethods())
            {
                var method = mdReader.GetMethodDefinition(methodHandle);
                Console.WriteLine($"  {mdReader.GetString(method.Name)}");
            }
        });

        // ==================== TASK 2: AudioController ====================
        Console.WriteLine();
        Console.WriteLine("=== TASK 2: AudioController — all methods ===");
        InspectDll(asmCsharp, mdReader =>
        {
            bool found = false;
            foreach (var typeHandle in mdReader.TypeDefinitions)
            {
                var typeDef = mdReader.GetTypeDefinition(typeHandle);
                if (mdReader.GetString(typeDef.Name) != "AudioController") continue;
                found = true;
                Console.WriteLine($"Namespace: {mdReader.GetString(typeDef.Namespace)}");

                var volKws = new[] { "volume", "setmaster", "getmaster", "mastervol", "setlevel", "setbus", "getvolume", "setvolume" };
                Console.WriteLine("Volume/level/master/bus matches:");
                foreach (var methodHandle in typeDef.GetMethods())
                {
                    var method = mdReader.GetMethodDefinition(methodHandle);
                    var mn = mdReader.GetString(method.Name);
                    var mnL = mn.ToLowerInvariant();
                    foreach (var kw in volKws)
                        if (mnL.Contains(kw)) { Console.WriteLine($"  [MATCH] {mn}"); break; }
                }

                Console.WriteLine("All methods:");
                foreach (var methodHandle in typeDef.GetMethods())
                {
                    var method = mdReader.GetMethodDefinition(methodHandle);
                    Console.WriteLine($"  {mdReader.GetString(method.Name)}");
                }
                break;
            }
            if (!found) Console.WriteLine("AudioController NOT FOUND");
        });

        // ==================== TASK 3: FMODUnity RuntimeManager ====================
        Console.WriteLine();
        Console.WriteLine("=== TASK 3: FMODUnity.dll — RuntimeManager all methods ===");
        InspectDll(fmodDll, mdReader =>
        {
            bool found = false;
            foreach (var typeHandle in mdReader.TypeDefinitions)
            {
                var typeDef = mdReader.GetTypeDefinition(typeHandle);
                if (mdReader.GetString(typeDef.Name) != "RuntimeManager") continue;
                found = true;
                Console.WriteLine($"Namespace: {mdReader.GetString(typeDef.Namespace)}");
                Console.WriteLine("All methods:");
                foreach (var methodHandle in typeDef.GetMethods())
                {
                    var method = mdReader.GetMethodDefinition(methodHandle);
                    Console.WriteLine($"  [{method.Attributes}] {mdReader.GetString(method.Name)}");
                }
                break;
            }
            if (!found) Console.WriteLine("RuntimeManager NOT FOUND");

            Console.WriteLine();
            Console.WriteLine("All FMODUnity types/methods matching bus/vca/volume/master/studio/setvol:");
            var kwds = new[] { "setvolume", "getmaster", "getvca", "getbus", "studiosystem", "bus", "vca", "volume", "master" };
            var seen = new HashSet<string>();
            foreach (var typeHandle in mdReader.TypeDefinitions)
            {
                var typeDef = mdReader.GetTypeDefinition(typeHandle);
                var typeName = mdReader.GetString(typeDef.Name);
                foreach (var methodHandle in typeDef.GetMethods())
                {
                    var method = mdReader.GetMethodDefinition(methodHandle);
                    var mn = mdReader.GetString(method.Name);
                    var mnL = mn.ToLowerInvariant();
                    foreach (var kw in kwds)
                    {
                        if (mnL.Contains(kw))
                        {
                            var key = $"{typeName}.{mn}";
                            if (seen.Add(key)) Console.WriteLine($"  {key}");
                            break;
                        }
                    }
                }
            }
        });
    }
}
