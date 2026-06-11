using System.Text;
using builder;

/// <summary>
/// Generates the package.xml Visual Studio property-page schema that exposes
/// a "Linkage" drop-down in the VS project properties UI, analogous to the
/// one used by libbitcoin/secp256k1.
/// </summary>
internal static class PackageXml
{
    public static void Generate(string packageDir)
    {
        string targetsDir = Path.Combine(packageDir, "build", "native");
        Directory.CreateDirectory(targetsDir);

        string content =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <!--
            ##################################################################
            #   GENERATED SOURCE CODE, DO NOT EDIT EXCEPT EXPERIMENTALLY    #
            ##################################################################
            -->
            <ProjectSchemaDefinitions xmlns="clr-namespace:Microsoft.Build.Framework.XamlTypes;assembly=Microsoft.Build.Framework">
              <Rule Name="Linkage-ultrafast-uiextension" PageTemplate="tool"
                    DisplayName="NuGet Dependencies" SwitchPrefix="/" Order="1">
                <Rule.Categories>
                  <Category Name="UltrafastSecp256k1" DisplayName="UltrafastSecp256k1" />
                </Rule.Categories>
                <Rule.DataSource>
                  <DataSource Persistence="ProjectFile" ItemType="" />
                </Rule.DataSource>
                <EnumProperty Name="Linkage-ultrafast"
                              DisplayName="Linkage"
                              Description="How NuGet UltrafastSecp256k1 will be linked into the output of this project"
                              Category="UltrafastSecp256k1">
                  <EnumValue Name=""        DisplayName="Not linked" />
                  <EnumValue Name="static"  DisplayName="Static (LIB)" />
                  <EnumValue Name="ltcg"    DisplayName="Static LTCG (/GL, link /LTCG)" />
                  <EnumValue Name="dynamic" DisplayName="Dynamic (DLL)" />
                </EnumProperty>
              </Rule>
            </ProjectSchemaDefinitions>
            """;

        string path = Path.Combine(targetsDir, "package.xml");
        File.WriteAllText(path, content, Encoding.UTF8);
        Console.WriteLine($"Written: {path}");
    }
}
