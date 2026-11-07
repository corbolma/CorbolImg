// Versione condivisa da AppImg.exe e dall'installer. Cambiala qui prima di una nuova distribuzione.
using System.Reflection;

[assembly: AssemblyProduct("AppImg")]
[assembly: AssemblyCopyright(AppImg.AppVersion.Copyright)]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace AppImg
{
    static class AppVersion
    {
        // Autore e licenza: compaiono nelle proprietà degli exe e nella finestra dell'installer
        public const string Author = "Mauro Corboli";
        public const string Copyright = "Copyright © 2026 " + Author + " - MIT License";
        public const string RepoUrl = "https://github.com/corbolma/AppImg";
        public const string Text = "1.1.0";
    }
}
