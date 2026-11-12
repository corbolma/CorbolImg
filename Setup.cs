// Installer di CorbolImg: un solo exe che contiene CorbolImg.exe.
// Installa per l'utente corrente (nessun permesso di amministratore) in %LOCALAPPDATA%\Programs\CorbolImg.
//   CorbolImg-Setup.exe              -> finestra di installazione / aggiornamento
//   CorbolImg-Setup.exe /silent      -> installazione silenziosa (menu Start, "Invia a", "Apri con")
//   Uninstall.exe /uninstall      -> disinstallazione (registrata in Impostazioni > App)
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Installazione CorbolImg")]

namespace CorbolImg.Setup
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Func<string, bool> has = a => args.Any(x => x.Equals(a, StringComparison.OrdinalIgnoreCase));
            bool silent = has("/silent") || has("/s");
            L.LoadFromSettings(); // stessa lingua scelta nell'app, altrimenti quella di Windows
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (has("/uninstall")) return Installer.Uninstall(silent) ? 0 : 1;
                if (silent)
                {
                    // In silenzioso non si torna mai a una versione più vecchia, salvo /force.
                    var inst = Installer.InstalledVersion();
                    if (inst != null && inst > Installer.Current && !has("/force")) return 3;
                    Installer.Install(Installer.DesktopShortcutExists(), Installer.SendToEnabled(), Installer.AssociationsEnabled(), false);
                    return 0;
                }
                Application.Run(new SetupForm());
                return 0;
            }
            catch (Exception ex)
            {
                if (!silent) MessageBox.Show(L.T("Errore", "Error") + ": " + ex.Message, "CorbolImg", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
        }
    }

    static class Installer
    {
        const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CorbolImg";

        public static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CorbolImg");
        public static readonly string ExePath = Path.Combine(InstallDir, "CorbolImg.exe");
        public static readonly string UninstallerPath = Path.Combine(InstallDir, "Uninstall.exe");
        static readonly string StartMenuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "CorbolImg.lnk");
        static readonly string DesktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "CorbolImg.lnk");
        static readonly string SendToLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "CorbolImg.lnk");

        public static readonly Version Current = Version.Parse(AppVersion.Text);

        // Versione realmente installata: letta dall'exe (non solo dal registro), null se non installata.
        public static Version InstalledVersion()
        {
            if (!File.Exists(ExePath)) return OldApp.InstalledVersion(); // installata col nome vecchio, o null
            try
            {
                var fv = FileVersionInfo.GetVersionInfo(ExePath);
                if (fv.FileMajorPart + fv.FileMinorPart + fv.FileBuildPart > 0)
                    return new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart);
            }
            catch { }
            using (var k = Registry.CurrentUser.OpenSubKey(UninstKey))
            {
                Version v;
                string s = k != null ? k.GetValue("DisplayVersion") as string : null;
                return s != null && Version.TryParse(s, out v) ? v : new Version(0, 0, 0);
            }
        }

        public static string V(Version v) { return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build); }

        public static bool DesktopShortcutExists() { return File.Exists(DesktopLnk) || (!File.Exists(ExePath) && File.Exists(OldApp.DesktopLnk)); }
        // "Invia a" attivo di default; in un aggiornamento si mantiene la scelta fatta
        public static bool SendToEnabled()
        {
            if (!File.Exists(ExePath) && OldApp.Installed) return File.Exists(OldApp.SendToLnk); // aggiornamento dal nome vecchio
            return InstalledVersion() == null || File.Exists(SendToLnk);
        }
        // Registrazione come visualizzatore ("Apri con", App predefinite): attiva di default, in un aggiornamento si mantiene
        public static bool AssociationsEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstKey))
            {
                if (k == null) { var old = OldApp.Choice("OpenWith"); return old == null || old.Value != 0; } // anche dal nome vecchio
                return !(k.GetValue("OpenWith") is int) || (int)k.GetValue("OpenWith") != 0;
            }
        }

        // ---------- associazione ai tipi di immagine (solo per l'utente corrente) ----------
        const string ClassesKey = @"Software\Classes";
        const string ProgId = "AppImg.Image"; // come prima del cambio di nome: resta valida l'app predefinita scelta dall'utente
        const string CapabilitiesKey = @"Software\CorbolImg\Capabilities";
        static readonly string[] ImageExts =
        {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif",
            ".avif", ".jxr", ".wdp", ".hdp", ".jxl", ".dng", ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".orf",
            ".rw2", ".raf", ".pef", ".srw", ".erf", ".kdc", ".mrw", ".3fr", ".rwl", ".iiq"
        };

        [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

        // CorbolImg compare in "Apri con" e in Impostazioni > App predefinite. L'app predefinita la sceglie l'utente:
        // Windows non permette ai programmi di impostarla da soli.
        static void RegisterAssociations()
        {
            string open = "\"" + ExePath + "\" \"%1\"";
            using (var k = Registry.CurrentUser.CreateSubKey(ClassesKey + "\\" + ProgId))
            {
                k.SetValue("", L.T("Immagine", "Image"));
                k.SetValue("FriendlyTypeName", L.T("Immagine", "Image"));
                using (var i = k.CreateSubKey("DefaultIcon")) i.SetValue("", "\"" + ExePath + "\",0");
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", open);
            }
            using (var k = Registry.CurrentUser.CreateSubKey(ClassesKey + @"\Applications\CorbolImg.exe"))
            {
                k.SetValue("FriendlyAppName", "CorbolImg");
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", open);
                using (var t = k.CreateSubKey("SupportedTypes")) foreach (var e in ImageExts) t.SetValue(e, "");
            }
            foreach (var e in ImageExts)
                using (var k = Registry.CurrentUser.CreateSubKey(ClassesKey + "\\" + e + @"\OpenWithProgids"))
                    k.SetValue(ProgId, new byte[0], RegistryValueKind.None);
            using (var k = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
            {
                k.SetValue("ApplicationName", "CorbolImg");
                k.SetValue("ApplicationDescription", L.T("Visualizzatore di immagini con convertitore", "Image viewer and converter"));
                k.SetValue("ApplicationIcon", "\"" + ExePath + "\",0");
                using (var f = k.CreateSubKey("FileAssociations")) foreach (var e in ImageExts) f.SetValue(e, ProgId);
            }
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications")) k.SetValue("CorbolImg", CapabilitiesKey);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
        }

        static void UnregisterAssociations()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(ClassesKey + "\\" + ProgId, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(ClassesKey + @"\Applications\CorbolImg.exe", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\CorbolImg", false); } catch { }
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true)) if (k != null) k.DeleteValue("CorbolImg", false); } catch { }
            foreach (var e in ImageExts)
                try { using (var k = Registry.CurrentUser.OpenSubKey(ClassesKey + "\\" + e + @"\OpenWithProgids", true)) if (k != null) k.DeleteValue(ProgId, false); } catch { }
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }

        // Chiude CorbolImg se è aperta: prima con garbo (chiusura della finestra), poi forzando.
        public static void CloseRunning()
        {
            OldApp.CloseRunning(null); // la versione col nome vecchio
            int session = Process.GetCurrentProcess().SessionId;
            Func<Process[]> mine = () => Process.GetProcessesByName("CorbolImg").Where(p => p.SessionId == session).ToArray();
            if (mine().Length == 0) return;
            foreach (var p in mine())
                try { p.CloseMainWindow(); } catch { }
            var until = DateTime.Now.AddSeconds(5);
            while (mine().Length > 0 && DateTime.Now < until) Thread.Sleep(200);
            foreach (var p in mine())
                try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        public static void Install(bool desktop, bool sendTo, bool associate, bool launch)
        {
            CloseRunning();
            // Dal nome vecchio: dati nelle cartelle nuove, vecchia installazione tolta (le scelte sono già state lette)
            OldApp.MoveData();
            OldApp.RemoveInstall();
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\" + OldApp.Name, false); } catch { }
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true)) if (k != null) k.DeleteValue(OldApp.Name, false); } catch { }
            Directory.CreateDirectory(InstallDir);

            using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("CorbolImg.payload"))
            {
                if (src == null) throw new InvalidOperationException("pacchetto CorbolImg.exe mancante nell'installer");
                string tmp = ExePath + ".new";
                using (var dst = File.Create(tmp)) src.CopyTo(dst);
                if (File.Exists(ExePath)) File.Delete(ExePath);
                File.Move(tmp, ExePath);
            }

            string self = Application.ExecutablePath;
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(UninstallerPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, UninstallerPath, true);

            string desc = L.T("Visualizza e converte immagini", "View and convert images");
            CreateShortcut(StartMenuLnk, ExePath, "", desc);
            if (desktop) CreateShortcut(DesktopLnk, ExePath, "", desc);
            else if (File.Exists(DesktopLnk)) File.Delete(DesktopLnk);
            // "Invia a > CorbolImg" apre direttamente il convertitore con i file selezionati
            if (sendTo) CreateShortcut(SendToLnk, ExePath, "/convert", L.T("Converti le immagini selezionate con CorbolImg", "Convert the selected images with CorbolImg"));
            else if (File.Exists(SendToLnk)) File.Delete(SendToLnk);
            if (associate) RegisterAssociations();
            else UnregisterAssociations();

            using (var k = Registry.CurrentUser.CreateSubKey(UninstKey))
            {
                k.SetValue("DisplayName", "CorbolImg");
                k.SetValue("DisplayVersion", AppVersion.Text);
                k.SetValue("VersionMajor", Current.Major, RegistryValueKind.DWord);
                k.SetValue("VersionMinor", Current.Minor, RegistryValueKind.DWord);
                k.SetValue("Publisher", AppVersion.Author);
                k.SetValue("URLInfoAbout", AppVersion.RepoUrl);
                k.SetValue("DisplayIcon", ExePath + ",0");
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + UninstallerPath + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + UninstallerPath + "\" /uninstall /silent");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", (int)(new FileInfo(ExePath).Length / 1024 + new FileInfo(UninstallerPath).Length / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("OpenWith", associate ? 1 : 0, RegistryValueKind.DWord); // scelta ricordata negli aggiornamenti
            }

            UpdateOldAppKey(ImageExts);
            RetargetPins();
            if (launch) Process.Start(new ProcessStartInfo(ExePath) { WorkingDirectory = InstallDir });
        }

        public static bool Uninstall(bool silent)
        {
            if (!silent && MessageBox.Show(L.T("Vuoi disinstallare CorbolImg?", "Do you want to uninstall CorbolImg?"), L.T("Disinstalla CorbolImg", "Uninstall CorbolImg"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return false;

            CloseRunning();
            foreach (var f in new[] { StartMenuLnk, DesktopLnk, SendToLnk })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstKey, false); } catch { }
            UnregisterAssociations();
            try
            {
                string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CorbolImg");
                if (Directory.Exists(settings)) Directory.Delete(settings, true);
            }
            catch { }
            try
            {
                // Real-ESRGAN scaricato dalla Griglia IA
                string engine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CorbolImg");
                if (Directory.Exists(engine)) Directory.Delete(engine, true);
            }
            catch { }
            try { if (File.Exists(ExePath)) File.Delete(ExePath); } catch { }

            // L'uninstaller gira dalla cartella da eliminare: la cancella un cmd dopo la nostra uscita.
            Process.Start(new ProcessStartInfo("cmd.exe",
                "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + InstallDir + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath()
            });

            if (!silent) MessageBox.Show(L.T("CorbolImg è stata disinstallata.", "CorbolImg has been uninstalled."), L.T("Disinstalla CorbolImg", "Uninstall CorbolImg"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }

        // "Apri con" col nome vecchio: se è l'app predefinita scelta dall'utente per qualche tipo di file
        // (Apri con > Usa sempre questa app), la voce resta e apre il nuovo exe, altrimenti si toglie
        static void UpdateOldAppKey(string[] exts)
        {
            string key = @"Software\Classes\Applications\" + OldApp.Name + ".exe";
            string id = @"Applications\" + OldApp.Name + ".exe";
            bool chosen = false;
            foreach (var e in exts)
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + e + @"\UserChoice"))
                        if (k != null && string.Equals(k.GetValue("ProgId") as string, id, StringComparison.OrdinalIgnoreCase)) chosen = true;
                }
                catch { }
            if (!chosen)
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(key, false); } catch { }
                return;
            }
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(key))
                {
                    k.SetValue("FriendlyAppName", "CorbolImg");
                    try { k.DeleteSubKeyTree("SupportedTypes", false); } catch { } // niente doppioni in "Apri con"
                    using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", "\"" + ExePath + "\" \"%1\"");
                }
            }
            catch { }
        }

        // Icone fissate alla barra delle applicazioni che aprono l'exe col nome vecchio: ora aprono quello nuovo
        // (stesso AppUserModelID, quindi l'icona fissata resta quella della finestra aperta)
        static void RetargetPins()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            if (!Directory.Exists(dir)) return;
            foreach (var lnk in Directory.GetFiles(dir, "*.lnk"))
            {
                IShellLinkW link = null;
                try
                {
                    link = (IShellLinkW)new CShellLink();
                    var pf = (System.Runtime.InteropServices.ComTypes.IPersistFile)link;
                    pf.Load(lnk, 2 /* STGM_READWRITE */);
                    var sb = new System.Text.StringBuilder(1024);
                    link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                    if (string.Equals(sb.ToString(), OldApp.ExePath, StringComparison.OrdinalIgnoreCase))
                    {
                        link.SetPath(ExePath);
                        link.SetWorkingDirectory(InstallDir);
                        link.SetIconLocation(ExePath, 0);
                        pf.Save(lnk, true);
                    }
                }
                catch { }
                finally { if (link != null) Marshal.FinalReleaseComObject(link); }
            }
        }

        // Deve coincidere con Program.AppUserModelId in CorbolImg.cs
        const string AppUserModelId = "AppImg.Viewer"; // come prima del cambio di nome: le icone già fissate alla barra restano della stessa app

        // Crea il collegamento con l'AppUserModelID, così l'icona fissata alla barra corrisponde alla finestra aperta.
        static void CreateShortcut(string lnkPath, string target, string args, string description)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath));
            var link = (IShellLinkW)new CShellLink();
            try
            {
                link.SetPath(target);
                if (args.Length > 0) link.SetArguments(args);
                link.SetWorkingDirectory(Path.GetDirectoryName(target));
                link.SetDescription(description);
                link.SetIconLocation(target, 0);

                var store = (IPropertyStore)link;
                var key = new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
                var pv = new PropVariant { vt = 31 /* VT_LPWSTR */, p = Marshal.StringToCoTaskMemUni(AppUserModelId) };
                try { store.SetValue(ref key, ref pv); store.Commit(); }
                finally { Marshal.FreeCoTaskMem(pv.p); }

                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(lnkPath, true);
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        interface IPropertyStore
        {
            void GetCount(out uint cProps);
            void GetAt(uint iProp, out PropertyKey pkey);
            void GetValue(ref PropertyKey key, out PropVariant pv);
            void SetValue(ref PropertyKey key, ref PropVariant pv);
            void Commit();
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PropertyKey { public Guid fmtid; public uint pid; }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr p;
        }
    }

    class SetupForm : Form
    {
        readonly CheckBox cbAssoc, cbSendTo, cbDesktop, cbLaunch;
        readonly Button btnInstall, btnCancel;
        readonly Label lblStatus;

        public SetupForm()
        {
            var installed = Installer.InstalledVersion();
            var cur = Installer.Current;
            string vi = installed != null ? Installer.V(installed) : null, vc = Installer.V(cur);
            string infoText, action;
            bool warn = false;
            if (installed != null && !File.Exists(Installer.ExePath))
            {
                action = L.T("Aggiorna", "Update");
                infoText = L.F("{0} {1} diventa CorbolImg {2}: cambia solo il nome, le impostazioni vengono mantenute.",
                               "{0} {1} becomes CorbolImg {2}: only the name changes, your settings are kept.", OldApp.Name, vi, vc);
            }
            else if (installed == null)
            {
                action = L.T("Installa", "Install");
                infoText = L.T("CorbolImg verrà installata solo per il tuo utente, senza permessi di amministratore, nella cartella:",
                               "CorbolImg will be installed for your user only, without administrator rights, in the folder:");
            }
            else if (installed < cur)
            {
                action = L.T("Aggiorna", "Update");
                infoText = L.F("Aggiornamento dalla versione {0} alla {1}. Le impostazioni vengono mantenute.",
                               "Update from version {0} to {1}. Your settings will be kept.", vi, vc);
            }
            else if (installed == cur)
            {
                action = L.T("Reinstalla", "Reinstall");
                infoText = L.F("La versione {0} è già installata. Puoi reinstallarla (le impostazioni vengono mantenute).",
                               "Version {0} is already installed. You can reinstall it (your settings will be kept).", vc);
            }
            else
            {
                action = L.T("Installa comunque", "Install anyway");
                warn = true;
                infoText = L.F("Attenzione: è installata una versione più recente ({0}). Questo installer contiene la {1}, più vecchia. Continuando la sostituirai.",
                           "Warning: a newer version ({0}) is installed. This installer contains the older {1}. Continuing will replace it.", vi, vc);
            }

            var accent = Color.FromArgb(124, 58, 237);
            Text = action + " CorbolImg";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96f, 96f); // layout pensato a 96 DPI, scalato in base allo schermo
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(470, 300);
            BackColor = Color.White;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var header = new Panel { Dock = DockStyle.Top, Height = 86, BackColor = accent };
            var pic = new PictureBox { Location = new Point(20, 15), Size = new Size(56, 56), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
            try { using (var ic = new Icon(Icon, 64, 64)) pic.Image = ic.ToBitmap(); } catch { }
            var title = new Label
            {
                Text = "CorbolImg " + AppVersion.Text, ForeColor = Color.White, AutoSize = true,
                Font = new Font("Segoe UI Semibold", 15f), Location = new Point(88, 16), BackColor = Color.Transparent
            };
            var sub = new Label
            {
                Text = L.T("Visualizzatore di immagini, con convertitore: JPEG, PNG, HEIC, WebP, TIFF, GIF, RAW, ICO, PDF e altri",
                           "Image viewer, with converter: JPEG, PNG, HEIC, WebP, TIFF, GIF, RAW, ICO, PDF and more"),
                ForeColor = Color.FromArgb(237, 228, 255),
                AutoSize = true, MaximumSize = new Size(370, 0), Location = new Point(90, 50), BackColor = Color.Transparent
            };
            header.Controls.AddRange(new Control[] { pic, title, sub });

            var info = new Label
            {
                Location = new Point(20, 100), AutoSize = true, MaximumSize = new Size(430, 0),
                Text = infoText, ForeColor = warn ? Color.FromArgb(190, 60, 0) : SystemColors.ControlText
            };
            var path = new Label
            {
                Location = new Point(20, 136), Size = new Size(430, 18), ForeColor = Color.DimGray,
                Text = Installer.InstallDir, AutoEllipsis = true
            };

            cbAssoc = new CheckBox
            {
                Text = L.T("Proponi CorbolImg in \"Apri con\" per le immagini", "Offer CorbolImg in \"Open with\" for images"),
                Location = new Point(22, 164), AutoSize = true, Checked = Installer.AssociationsEnabled()
            };
            cbSendTo = new CheckBox
            {
                Text = L.T("Aggiungi CorbolImg al menu \"Invia a\" di Esplora file", "Add CorbolImg to the File Explorer \"Send to\" menu"),
                Location = new Point(22, 164), AutoSize = true, Checked = Installer.SendToEnabled()
            };
            cbDesktop = new CheckBox { Text = L.T("Crea un collegamento sul desktop", "Create a desktop shortcut"), Location = new Point(22, 188), AutoSize = true, Checked = Installer.DesktopShortcutExists() };
            cbLaunch = new CheckBox { Text = L.T("Avvia CorbolImg al termine", "Launch CorbolImg when finished"), Location = new Point(22, 212), AutoSize = true, Checked = true };

            lblStatus = new Label { Location = new Point(20, 262), Size = new Size(240, 20), ForeColor = Color.DimGray };
            btnInstall = new Button { Text = action, Location = new Point(240, 256), MinimumSize = new Size(120, 30), AutoSize = true };
            btnCancel = new Button { Text = L.T("Annulla", "Cancel"), Location = new Point(366, 256), MinimumSize = new Size(90, 30), AutoSize = true, DialogResult = DialogResult.Cancel };
            btnInstall.Click += delegate { DoInstall(); };
            // DialogResult da solo non chiude: l'installer è la finestra principale, non un dialogo
            btnCancel.Click += delegate { Close(); };
            AcceptButton = btnInstall;
            CancelButton = btnCancel;

            // Piè di pagina: autore, licenza e link al progetto
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Color.FromArgb(244, 246, 249) };
            string owner = "© 2026 " + AppVersion.Author + "  ·  ";
            string lic = L.T("Licenza MIT", "MIT License");
            var credits = new LinkLabel
            {
                Text = owner + lic, AutoSize = true, Location = new Point(18, 7), ForeColor = Color.DimGray,
                LinkColor = accent, ActiveLinkColor = accent,
                LinkBehavior = LinkBehavior.AlwaysUnderline, BackColor = Color.Transparent
            };
            credits.LinkArea = new LinkArea(owner.Length, lic.Length);
            credits.LinkClicked += delegate { OpenUrl(AppVersion.RepoUrl + "/blob/main/LICENSE"); };
            var site = new LinkLabel
            {
                Text = "GitHub", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right,
                LinkColor = accent, ActiveLinkColor = accent,
                LinkBehavior = LinkBehavior.AlwaysUnderline, BackColor = Color.Transparent
            };
            site.LinkClicked += delegate { OpenUrl(AppVersion.RepoUrl); };
            footer.Controls.AddRange(new Control[] { credits, site });

            Controls.AddRange(new Control[] { header, info, path, cbAssoc, cbSendTo, cbDesktop, cbLaunch, lblStatus, btnInstall, btnCancel, footer });

            // Dispone i controlli sotto il testo informativo, qualunque sia la sua altezza (righe, DPI)
            Load += delegate
            {
                header.Height = Math.Max(header.Height, sub.Bottom + 12);
                info.Top = header.Bottom + 14;
                int y = info.Bottom + 6;
                path.Top = y; y = path.Bottom + 12;
                foreach (var cb in new[] { cbAssoc, cbSendTo, cbDesktop, cbLaunch }) { cb.Top = y; y = cb.Bottom + 4; }
                y += 14;
                btnInstall.Top = btnCancel.Top = y;
                // Pulsanti allineati a destra: si allargano se la traduzione è lunga
                btnCancel.Left = ClientSize.Width - 14 - btnCancel.Width;
                btnInstall.Left = btnCancel.Left - 6 - btnInstall.Width;
                lblStatus.Width = Math.Max(60, btnInstall.Left - 26);
                lblStatus.Top = y + 6;
                ClientSize = new Size(ClientSize.Width, btnInstall.Bottom + 14 + footer.Height);
                site.Location = new Point(footer.ClientSize.Width - 18 - site.Width, credits.Top);
            };
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        void DoInstall()
        {
            btnInstall.Enabled = btnCancel.Enabled = false;
            lblStatus.Text = L.T("Installazione in corso...", "Installing...");
            Cursor = Cursors.WaitCursor;
            Application.DoEvents();
            try
            {
                Installer.Install(cbDesktop.Checked, cbSendTo.Checked, cbAssoc.Checked, cbLaunch.Checked);
                Cursor = Cursors.Default;
                string done = L.T("CorbolImg è stata installata.\n\n" +
                        "La trovi nel menu Start. Per convertire più immagini: selezionale in Esplora file → tasto destro → " +
                        "Invia a → CorbolImg.\n\n" +
                        "Per disinstallarla: Impostazioni → App → App installate → CorbolImg.",
                        "CorbolImg has been installed.\n\n" +
                        "You can find it in the Start menu. To convert several images: select them in File Explorer → right-click → " +
                        "Send to → CorbolImg.\n\n" +
                        "To uninstall it: Settings → Apps → Installed apps → CorbolImg.");
                if (cbAssoc.Checked)
                {
                    // Windows non permette ai programmi di diventare predefiniti da soli: si apre la pagina giusta delle Impostazioni
                    if (MessageBox.Show(this, done + "\n\n" + L.T("Vuoi aprire le immagini con CorbolImg quando fai doppio clic? Si aprirà Impostazioni → App predefinite: scegli CorbolImg per i tipi di file che vuoi.",
                                                                  "Do you want images to open in CorbolImg when you double-click them? Settings → Default apps will open: choose CorbolImg for the file types you want."),
                            "CorbolImg", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        OpenUrl("ms-settings:defaultapps?registeredAppUser=CorbolImg");
                }
                else MessageBox.Show(this, done, "CorbolImg", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                lblStatus.Text = "";
                btnInstall.Enabled = btnCancel.Enabled = true;
                MessageBox.Show(this, L.T("Installazione non riuscita:", "Installation failed:") + "\n" + ex.Message, "CorbolImg", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
