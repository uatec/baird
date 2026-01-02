using System;
using System.Linq;
using System.Reflection;

class Program
{
    static void Main()
    {
        try
        {
            // Try to load the assembly
            var assembly = Assembly.Load("Jellyfin.Sdk");
            
            Console.WriteLine($"Loaded Assembly: {assembly.FullName}");

            var types = assembly.GetTypes();

            Console.WriteLine("\n--- ApiClientBuilder Members ---");
            var builderType = types.FirstOrDefault(t => t.Name.Contains("ApiClientBuilder")); // Fuzzy match
            if(builderType != null) {
                 Console.WriteLine($"Type: {builderType.FullName}");
                 foreach(var m in builderType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                    Console.WriteLine(m);
            }

            Console.WriteLine("\n--- AuthenticateByNameRequestBuilder Members ---");
            // Need to find the exact type name, might be nested or generated
            var authBuilderType = types.FirstOrDefault(t => t.Name.Contains("AuthenticateByNameRequestBuilder"));
            if(authBuilderType != null) {
                 Console.WriteLine($"Type: {authBuilderType.FullName}");
                 foreach(var m in authBuilderType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if(m.Name.StartsWith("Post"))
                        Console.WriteLine($"Method: {m.Name} Returns: {m.ReturnType.Name}");
            }

        }
        catch(Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            // print all loaded assemblies
            foreach(var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Console.WriteLine($"Loaded: {a.FullName}");
            }
        }
    }
}
