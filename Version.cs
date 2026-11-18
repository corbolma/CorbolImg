// Versione condivisa da CorbolImg.exe e dall'installer. Cambiala qui prima di una nuova distribuzione.
using System.Reflection;

[assembly: AssemblyProduct("CorbolImg")]
[assembly: AssemblyCopyright(CorbolImg.AppVersion.Copyright)]
[assembly: AssemblyVersion("1.7.1.0")]
[assembly: AssemblyFileVersion("1.7.1.0")]

namespace CorbolImg
{
    static class AppVersion
    {
        // Autore e licenza: compaiono nelle proprietà degli exe e nella finestra dell'installer
        public const string Author = "Mauro Corboli";
        public const string Copyright = "Copyright © 2026 " + Author + " - MIT License";
        public const string RepoUrl = "https://github.com/corbolma/CorbolImg";
        public const string Text = "1.7.1";
    }
}
