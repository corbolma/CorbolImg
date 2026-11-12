// Assistente della Griglia IA: un modello linguistico che "pilota" Real-ESRGAN.
// L'utente scrive cosa vuole ("migliora F3 e salvalo", "dividi D2 in 4x4"), il modello guarda la scacchiera
// (immagine ridotta con la griglia e le etichette) e risponde con un JSON di azioni che CorbolImg esegue.
//   - Claude Code: il programma installato sul PC, con l'abbonamento dell'utente (niente chiave API).
//   - Claude: API Anthropic (POST /v1/messages), con la chiave dell'utente.
//   - Compatibile OpenAI: POST <url>/chat/completions (OpenAI, OpenRouter, Ollama, LM Studio...), modello con visione.
// Stesso schema di CorbolPad e CorbolIp. Niente SDK: HTTP e JSON a mano, con JavaScriptSerializer.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CorbolImg
{
    class AiMessage
    {
        public string Role, Text;   // Role: "user" o "assistant"
        public byte[] Jpeg;         // immagine allegata (solo messaggi "user"), o null
        public AiMessage(string role, string text, byte[] jpeg = null) { Role = role; Text = text; Jpeg = jpeg; }
    }

    class AiRequest
    {
        public string System;
        public List<AiMessage> Messages = new List<AiMessage>();
    }

    class AiResult
    {
        public string Text = "";
        public string Error;            // messaggio per l'utente, già tradotto; null = tutto bene
        public bool Cancelled;
        public bool Ok { get { return Error == null && !Cancelled; } }
    }

    interface IAiProvider
    {
        string Name { get; }
        // Bloccante: va chiamato da un thread in background.
        AiResult Run(AiRequest req, CancellationToken cancel);
    }

    static class AiJson
    {
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };

        public static object Parse(string json)
        {
            try { return Json.DeserializeObject(json); } catch { return null; }
        }

        // Get(obj, "a", "b", 0, "c") = obj.a.b[0].c, o null
        public static object Get(object o, params object[] path)
        {
            foreach (var p in path)
            {
                if (o == null) return null;
                if (p is string)
                {
                    var d = o as IDictionary<string, object>;
                    object v;
                    o = d != null && d.TryGetValue((string)p, out v) ? v : null;
                }
                else
                {
                    var a = o as IList;
                    int i = (int)p;
                    o = a != null && i < a.Count ? a[i] : null;
                }
            }
            return o;
        }

        public static string Str(object o, params object[] path)
        {
            var v = Get(o, path);
            return v == null ? null : v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static int Int(object o, string key, int def)
        {
            var v = Get(o, key);
            if (v is int) return (int)v;
            if (v is decimal) return (int)(decimal)v;
            if (v is double) return (int)(double)v;
            int r;
            return v is string && int.TryParse((string)v, out r) ? r : def;
        }

        // Il modello deve rispondere solo con un oggetto JSON; per sicurezza si prende il primo "{" e l'ultimo "}"
        public static object ExtractObject(string text)
        {
            if (text == null) return null;
            int a = text.IndexOf('{'), b = text.LastIndexOf('}');
            if (a < 0 || b <= a) return null;
            return Parse(text.Substring(a, b - a + 1)) as IDictionary<string, object>;
        }
    }

    // Parte comune delle API HTTP: richiesta JSON senza streaming (le risposte sono brevi), nuovi tentativi
    // se il servizio è sovraccarico, Stop che chiude la connessione.
    abstract class AiHttpProvider : IAiProvider
    {
        public abstract string Name { get; }
        protected abstract string Url { get; }
        protected abstract void AddHeaders(HttpWebRequest req);
        protected abstract Dictionary<string, object> Body(AiRequest r);
        protected abstract string TextFrom(object response, out string error);

        public AiResult Run(AiRequest r, CancellationToken cancel)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            var res = new AiResult();
            byte[] body;
            try { body = Encoding.UTF8.GetBytes(AiJson.Json.Serialize(Body(r))); }
            catch (Exception ex) { res.Error = ex.Message; return res; }

            for (int attempt = 0; ; attempt++)
            {
                HttpWebRequest req = null;
                try
                {
                    req = (HttpWebRequest)WebRequest.Create(Url);
                    req.Method = "POST";
                    req.ContentType = "application/json";
                    req.UserAgent = "CorbolImg/" + AppVersion.Text;
                    req.Timeout = req.ReadWriteTimeout = 300000;
                    req.ServicePoint.Expect100Continue = false;
                    AddHeaders(req);
                    using (cancel.Register(delegate { try { req.Abort(); } catch { } }))
                    {
                        using (var st = req.GetRequestStream()) st.Write(body, 0, body.Length);
                        using (var resp = (HttpWebResponse)req.GetResponse())
                        using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        {
                            string err;
                            res.Text = TextFrom(AiJson.Parse(sr.ReadToEnd()), out err) ?? "";
                            if (err != null) res.Error = Name + ": " + err;
                        }
                    }
                    return res;
                }
                catch (WebException ex)
                {
                    if (cancel.IsCancellationRequested) { res.Cancelled = true; return res; }
                    var http = ex.Response as HttpWebResponse;
                    if (http == null) { res.Error = ConnectionError(ex); return res; }
                    int status = (int)http.StatusCode;
                    string text = "";
                    try { using (var sr = new StreamReader(http.GetResponseStream(), Encoding.UTF8)) text = sr.ReadToEnd(); } catch { }
                    http.Close();
                    if ((status == 429 || status >= 500) && attempt < 2)
                    {
                        cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(2 << attempt));
                        if (cancel.IsCancellationRequested) { res.Cancelled = true; return res; }
                        continue;
                    }
                    res.Error = HttpError(status, ErrorMessage(text));
                    return res;
                }
                catch (Exception ex)
                {
                    if (cancel.IsCancellationRequested) { res.Cancelled = true; return res; }
                    res.Error = Name + ": " + ex.Message;
                    return res;
                }
            }
        }

        string ConnectionError(WebException ex)
        {
            switch (ex.Status)
            {
                case WebExceptionStatus.NameResolutionFailure:
                    return L.F("{0}: indirizzo non trovato. Controlla la connessione a Internet o l'indirizzo del servizio.",
                               "{0}: address not found. Check your Internet connection or the service address.", Name);
                case WebExceptionStatus.ConnectFailure:
                    return L.F("{0}: connessione rifiutata. Il servizio è avviato?", "{0}: connection refused. Is the service running?", Name);
                case WebExceptionStatus.Timeout:
                    return L.F("{0}: nessuna risposta (tempo scaduto).", "{0}: no response (timed out).", Name);
                default:
                    return Name + ": " + ex.Message;
            }
        }

        string HttpError(int status, string detail)
        {
            string what;
            if (status == 401) what = L.T("chiave API non valida o mancante", "invalid or missing API key");
            else if (status == 402) what = L.T("credito esaurito o problema di fatturazione", "out of credit or billing problem");
            else if (status == 403) what = L.T("accesso negato", "access denied");
            else if (status == 404) what = L.T("modello o indirizzo non trovato", "model or address not found");
            else if (status == 413) what = L.T("richiesta troppo grande", "request too large");
            else if (status == 429) what = L.T("troppe richieste: riprova tra poco", "too many requests: try again shortly");
            else if (status >= 500) what = L.T("servizio sovraccarico o in errore: riprova tra poco", "service overloaded or failing: try again shortly");
            else what = L.F("errore {0}", "error {0}", status);
            if (!string.IsNullOrEmpty(detail) && detail.Length < 400) what += " (" + detail.Trim() + ")";
            return Name + ": " + what;
        }

        static string ErrorMessage(string body)
        {
            var o = AiJson.Parse(body);
            return AiJson.Str(o, "error", "message") ?? (AiJson.Get(o, "error") as string) ?? AiJson.Str(o, "message")
                ?? (o == null && body != null && body.Length < 200 ? body : null);
        }
    }

    // API Anthropic: https://api.anthropic.com/v1/messages
    class ClaudeProvider : AiHttpProvider
    {
        public const string DefaultModel = "claude-opus-5-5";
        public static readonly string[] Models = { "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5" };
        const string ApiVersion = "2026-10-21";

        public string ApiKey, Model = DefaultModel;
        public override string Name { get { return "Claude"; } }
        protected override string Url { get { return "https://api.anthropic.com/v1/messages"; } }

        protected override void AddHeaders(HttpWebRequest req)
        {
            req.Headers["x-api-key"] = ApiKey ?? "";
            req.Headers["anthropic-version"] = ApiVersion;
        }

        public static object Content(AiMessage m)
        {
            if (m.Jpeg == null) return m.Text;
            return new object[]
            {
                new Dictionary<string, object> { { "type", "image" }, { "source", new Dictionary<string, object>
                    { { "type", "base64" }, { "media_type", "image/jpeg" }, { "data", Convert.ToBase64String(m.Jpeg) } } } },
                new Dictionary<string, object> { { "type", "text" }, { "text", m.Text } }
            };
        }

        protected override Dictionary<string, object> Body(AiRequest r)
        {
            var b = new Dictionary<string, object>
            {
                { "model", Model },
                { "max_tokens", 4000 },
                { "messages", r.Messages.Select(m => new Dictionary<string, object> { { "role", m.Role }, { "content", Content(m) } }).ToArray() }
            };
            if (!string.IsNullOrEmpty(r.System)) b["system"] = r.System;
            return b;
        }

        protected override string TextFrom(object o, out string error)
        {
            error = AiJson.Str(o, "stop_reason") == "refusal" ? L.T("ha rifiutato la richiesta.", "declined the request.") : null;
            var sb = new StringBuilder();
            var content = AiJson.Get(o, "content") as IList;
            if (content != null)
                foreach (var c in content) if (AiJson.Str(c, "type") == "text") sb.Append(AiJson.Str(c, "text"));
            return sb.ToString();
        }
    }

    // API "chat completions" compatibile OpenAI: <BaseUrl>/chat/completions
    class OpenAiProvider : AiHttpProvider
    {
        public string BaseUrl = "https://api.openai.com/v1", ApiKey, Model;

        public override string Name { get { try { return new Uri(BaseUrl).Host; } catch { return "OpenAI"; } } }
        protected override string Url { get { return BaseUrl.TrimEnd('/') + "/chat/completions"; } }

        protected override void AddHeaders(HttpWebRequest req)
        {
            if (!string.IsNullOrEmpty(ApiKey)) req.Headers[HttpRequestHeader.Authorization] = "Bearer " + ApiKey;
        }

        protected override Dictionary<string, object> Body(AiRequest r)
        {
            var msgs = new List<object>();
            if (!string.IsNullOrEmpty(r.System)) msgs.Add(new Dictionary<string, object> { { "role", "system" }, { "content", r.System } });
            foreach (var m in r.Messages)
            {
                object content = m.Text;
                if (m.Jpeg != null)
                    content = new object[]
                    {
                        new Dictionary<string, object> { { "type", "text" }, { "text", m.Text } },
                        new Dictionary<string, object> { { "type", "image_url" }, { "image_url", new Dictionary<string, object>
                            { { "url", "data:image/jpeg;base64," + Convert.ToBase64String(m.Jpeg) } } } }
                    };
                msgs.Add(new Dictionary<string, object> { { "role", m.Role }, { "content", content } });
            }
            return new Dictionary<string, object> { { "model", Model ?? "" }, { "messages", msgs } };
        }

        protected override string TextFrom(object o, out string error)
        {
            error = AiJson.Get(o, "error") != null ? (AiJson.Str(o, "error", "message") ?? L.T("errore", "error")) : null;
            return AiJson.Str(o, "choices", 0, "message", "content");
        }
    }

    // Claude Code installato sul PC, con l'abbonamento dell'utente. CorbolImg avvia "claude -p" senza strumenti
    // (non legge file né esegue comandi), senza server MCP e senza salvare la sessione, in una cartella vuota;
    // la conversazione e l'immagine arrivano su stdin in formato stream-json.
    class ClaudeCodeProvider : IAiProvider
    {
        public static readonly string[] Models = { "opus", "sonnet", "haiku" };
        public string Model = "";   // vuoto = il modello predefinito di Claude Code per l'account
        public string Name { get { return "Claude Code"; } }

        // claude.exe (installazione nativa) o claude.cmd (installazione con npm), o null
        public static string FindExe()
        {
            var dirs = new List<string>((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Where(d => d.Trim().Length > 0));
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dirs.Add(Path.Combine(home, ".local", "bin"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
                foreach (var d in dirs)
                {
                    try
                    {
                        string f = Path.Combine(d.Trim().Trim('"'), name);
                        if (File.Exists(f)) return f;
                    }
                    catch { }
                }
            return null;
        }

        // "claude -p" riceve un solo messaggio: quelli precedenti vanno scritti nel testo, prima dell'ultimo
        static string UserText(AiRequest r)
        {
            if (r.Messages.Count == 1) return r.Messages[0].Text;
            var sb = new StringBuilder("<conversation_so_far>\n");
            for (int i = 0; i < r.Messages.Count - 1; i++)
            {
                var m = r.Messages[i];
                string tag = m.Role == "assistant" ? "assistant" : "user";
                sb.Append('<').Append(tag).Append(">\n").Append(m.Text).Append("\n</").Append(tag).Append(">\n");
            }
            sb.Append("</conversation_so_far>\n\n").Append(r.Messages[r.Messages.Count - 1].Text);
            return sb.ToString();
        }

        static void KillTree(Process p)
        {
            try
            {
                if (p == null || p.HasExited) return;
                using (var k = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + p.Id + " /T /F") { UseShellExecute = false, CreateNoWindow = true }))
                    k.WaitForExit(5000);
            }
            catch { }
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        public AiResult Run(AiRequest r, CancellationToken cancel)
        {
            var res = new AiResult();
            string exe = FindExe();
            if (exe == null)
            {
                res.Error = L.T("Claude Code non è installato su questo PC (comando claude non trovato).", "Claude Code is not installed on this PC (claude command not found).");
                return res;
            }
            string dir = Path.Combine(Path.GetTempPath(), "CorbolImg-claudecode");
            string sysFile = Path.Combine(dir, "system-" + Guid.NewGuid().ToString("N") + ".txt");
            var err = new StringBuilder();
            Process p = null;
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(sysFile, r.System ?? "", new UTF8Encoding(false));
                string args = "-p --input-format stream-json --output-format stream-json --verbose --tools \"\" --no-session-persistence"
                            + " --strict-mcp-config --system-prompt-file \"" + sysFile + "\""
                            + (string.IsNullOrEmpty(Model) ? "" : " --model " + Model);
                bool cmd = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
                var psi = new ProcessStartInfo(cmd ? "cmd.exe" : exe, cmd ? "/c \"\"" + exe + "\" " + args + "\"" : args)
                {
                    WorkingDirectory = dir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                // se CorbolImg è stato avviato da una sessione di Claude Code, quella nuova deve essere indipendente
                psi.EnvironmentVariables.Remove("CLAUDECODE");
                psi.EnvironmentVariables.Remove("CLAUDE_CODE_ENTRYPOINT");
                p = Process.Start(psi);
                var proc = p;
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                p.BeginErrorReadLine();
                using (cancel.Register(delegate { KillTree(proc); }))
                {
                    var last = new AiMessage("user", UserText(r), r.Messages[r.Messages.Count - 1].Jpeg);
                    var line0 = AiJson.Json.Serialize(new Dictionary<string, object>
                    {
                        { "type", "user" },
                        { "message", new Dictionary<string, object> { { "role", "user" }, { "content", ClaudeProvider.Content(last) } } }
                    });
                    var input = Encoding.UTF8.GetBytes(line0 + "\n");
                    p.StandardInput.BaseStream.Write(input, 0, input.Length);
                    p.StandardInput.Close();

                    string line;
                    bool gotResult = false;
                    while ((line = p.StandardOutput.ReadLine()) != null)
                    {
                        var o = AiJson.Parse(line);
                        if (AiJson.Str(o, "type") != "result") continue;
                        gotResult = true;
                        bool isError = AiJson.Get(o, "is_error") is bool && (bool)AiJson.Get(o, "is_error");
                        string text = AiJson.Str(o, "result");
                        if (isError || AiJson.Str(o, "subtype") != "success")
                            res.Error = "Claude Code: " + (text ?? AiJson.Str(o, "subtype") ?? L.T("errore", "error"));
                        else res.Text = text ?? "";
                        break;
                    }
                    p.WaitForExit(10000);
                    if (cancel.IsCancellationRequested) { res.Cancelled = true; return res; }
                    if (!gotResult && res.Error == null)
                    {
                        string tail;
                        lock (err) tail = err.ToString().Trim();
                        if (tail.Length > 300) tail = tail.Substring(tail.Length - 300);
                        res.Error = L.F("Claude Code si è chiuso senza rispondere (codice {0}). Hai fatto il login in Claude Code?",
                                        "Claude Code closed without answering (code {0}). Are you logged in to Claude Code?", p.HasExited ? p.ExitCode : -1)
                                  + (tail.Length > 0 ? "\n" + tail : "");
                    }
                }
            }
            catch (Exception ex)
            {
                if (cancel.IsCancellationRequested) res.Cancelled = true;
                else res.Error = L.T("Impossibile avviare Claude Code:", "Could not start Claude Code:") + " " + ex.Message;
            }
            finally
            {
                KillTree(p);
                try { File.Delete(sysFile); } catch { }
            }
            return res;
        }
    }

    // Chiavi API in settings.txt cifrate con DPAPI: le legge solo lo stesso utente Windows sullo stesso PC
    static class Secret
    {
        // Non cambia con il nome dell'app (prima AppImg): con questa sono cifrate le chiavi già salvate
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AppImg.ai.v1");

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored) || !stored.StartsWith("dpapi:")) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(6)), Entropy, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }
    }

    // Impostazioni dell'assistente in settings.txt (chiavi "ai.*")
    static class AiSettings
    {
        public const string Claude = "claude", ClaudeCode = "claudecode", OpenAi = "openai";

        public static string Provider { get { return Settings.Get("ai.provider", ClaudeCode); } set { Settings.Set("ai.provider", value); } }
        public static string ClaudeKey { get { return Secret.Unprotect(Settings.Get("ai.claude.key", "")); } set { Settings.Set("ai.claude.key", Secret.Protect(value)); } }
        public static string ClaudeModel { get { return Settings.Get("ai.claude.model", ClaudeProvider.DefaultModel); } set { Settings.Set("ai.claude.model", value); } }
        public static string ClaudeCodeModel { get { return Settings.Get("ai.claudecode.model", ""); } set { Settings.Set("ai.claudecode.model", value); } }
        public static string OpenAiUrl { get { return Settings.Get("ai.openai.url", "https://api.openai.com/v1"); } set { Settings.Set("ai.openai.url", value); } }
        public static string OpenAiKey { get { return Secret.Unprotect(Settings.Get("ai.openai.key", "")); } set { Settings.Set("ai.openai.key", Secret.Protect(value)); } }
        public static string OpenAiModel { get { return Settings.Get("ai.openai.model", ""); } set { Settings.Set("ai.openai.model", value); } }
        // La scacchiera (immagine ridotta) viene mostrata al modello: senza, capisce solo le coordinate
        public static bool SendImage { get { return Settings.Get("ai.sendImage", true); } set { Settings.Set("ai.sendImage", value); } }

        // Provider configurato, o null con il motivo in "missing"
        public static IAiProvider Create(out string missing)
        {
            missing = null;
            if (Provider == Claude)
            {
                string key = ClaudeKey;
                if (key.Length > 0) return new ClaudeProvider { ApiKey = key, Model = ClaudeModel };
                missing = L.T("Manca la chiave API di Claude.", "The Claude API key is missing.");
                return null;
            }
            if (Provider == OpenAi)
            {
                if (OpenAiModel.Length > 0 && OpenAiUrl.Length > 0) return new OpenAiProvider { BaseUrl = OpenAiUrl, ApiKey = OpenAiKey, Model = OpenAiModel };
                missing = L.T("Manca l'indirizzo o il modello del servizio compatibile OpenAI.", "The OpenAI-compatible service address or model is missing.");
                return null;
            }
            return new ClaudeCodeProvider { Model = ClaudeCodeModel };
        }

        public static string Describe()
        {
            if (Provider == Claude) return "Claude API · " + ClaudeModel;
            if (Provider == OpenAi) return (OpenAiModel.Length > 0 ? OpenAiModel : "OpenAI") + " · " + OpenAiUrl;
            return "Claude Code" + (ClaudeCodeModel.Length > 0 ? " · " + ClaudeCodeModel : "");
        }
    }

    // Finestra delle impostazioni dell'assistente
    class AiSettingsForm : Form
    {
        readonly RadioButton rCode = new RadioButton(), rClaude = new RadioButton(), rOpen = new RadioButton();
        readonly ComboBox cCodeModel = new ComboBox(), cClaudeModel = new ComboBox();
        readonly TextBox tClaudeKey = new TextBox { UseSystemPasswordChar = true }, tUrl = new TextBox(), tKey = new TextBox { UseSystemPasswordChar = true }, tModel = new TextBox();
        readonly CheckBox chImage = new CheckBox();

        public AiSettingsForm()
        {
            Text = L.T("Assistente IA", "AI assistant");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.White;
            ClientSize = new Size(480, 470);

            var intro = new Label
            {
                AutoSize = true, MaximumSize = new Size(440, 0), Location = new Point(20, 14), ForeColor = Color.DimGray,
                Text = L.T("L'assistente legge le tue richieste e comanda Real-ESRGAN (che gira sul PC). Il miglioramento dei riquadri resta locale; all'assistente vanno solo il testo e, se lo permetti, la scacchiera ridotta.",
                           "The assistant reads your requests and drives Real-ESRGAN (which runs on this PC). Enhancing tiles stays local; the assistant only gets your text and, if you allow it, a reduced view of the grid.")
            };
            int y = 80;
            rCode.Text = L.T("Claude Code (abbonamento Claude, nessuna chiave)", "Claude Code (Claude subscription, no key)");
            rCode.Location = new Point(20, y); rCode.AutoSize = true;
            var lCodeModel = new Label { Text = L.T("Modello:", "Model:"), AutoSize = true, Location = new Point(40, y + 30) };
            cCodeModel.Location = new Point(140, y + 26); cCodeModel.Width = 200;
            cCodeModel.Items.Add(L.T("(predefinito)", "(default)")); cCodeModel.Items.AddRange(ClaudeCodeProvider.Models);
            y += 64;
            rClaude.Text = L.T("Claude (API Anthropic, con chiave)", "Claude (Anthropic API, with key)");
            rClaude.Location = new Point(20, y); rClaude.AutoSize = true;
            var lCk = new Label { Text = L.T("Chiave API:", "API key:"), AutoSize = true, Location = new Point(40, y + 30) };
            tClaudeKey.Location = new Point(140, y + 26); tClaudeKey.Width = 310;
            var lCm = new Label { Text = L.T("Modello:", "Model:"), AutoSize = true, Location = new Point(40, y + 60) };
            cClaudeModel.Location = new Point(140, y + 56); cClaudeModel.Width = 200; cClaudeModel.Items.AddRange(ClaudeProvider.Models);
            y += 94;
            rOpen.Text = L.T("Compatibile OpenAI (OpenAI, OpenRouter, Ollama, LM Studio...)", "OpenAI-compatible (OpenAI, OpenRouter, Ollama, LM Studio...)");
            rOpen.Location = new Point(20, y); rOpen.AutoSize = true;
            var lU = new Label { Text = L.T("Indirizzo:", "Address:"), AutoSize = true, Location = new Point(40, y + 30) };
            tUrl.Location = new Point(140, y + 26); tUrl.Width = 310;
            var lK = new Label { Text = L.T("Chiave API:", "API key:"), AutoSize = true, Location = new Point(40, y + 60) };
            tKey.Location = new Point(140, y + 56); tKey.Width = 310;
            var lM = new Label { Text = L.T("Modello:", "Model:"), AutoSize = true, Location = new Point(40, y + 90) };
            tModel.Location = new Point(140, y + 86); tModel.Width = 200;
            y += 126;
            chImage.Text = L.T("Mostra all'assistente la scacchiera (immagine ridotta con la griglia)", "Show the assistant the grid (reduced image with the grid)");
            chImage.Location = new Point(20, y); chImage.AutoSize = true;

            var ok = new Button { Text = "OK", Size = new Size(90, 30), Location = new Point(ClientSize.Width - 200, ClientSize.Height - 44) };
            var cancel = new Button { Text = L.T("Annulla", "Cancel"), Size = new Size(90, 30), Location = new Point(ClientSize.Width - 104, ClientSize.Height - 44), DialogResult = DialogResult.Cancel };
            ok.Click += delegate { SaveAll(); DialogResult = DialogResult.OK; Close(); };
            cancel.Click += delegate { Close(); };
            AcceptButton = ok; CancelButton = cancel;
            Controls.AddRange(new Control[] { intro, rCode, lCodeModel, cCodeModel, rClaude, lCk, tClaudeKey, lCm, cClaudeModel,
                                              rOpen, lU, tUrl, lK, tKey, lM, tModel, chImage, ok, cancel });

            string p = AiSettings.Provider;
            rCode.Checked = p == AiSettings.ClaudeCode; rClaude.Checked = p == AiSettings.Claude; rOpen.Checked = p == AiSettings.OpenAi;
            string cm = AiSettings.ClaudeCodeModel;
            if (cm.Length == 0) cCodeModel.SelectedIndex = 0; else cCodeModel.Text = cm;
            tClaudeKey.Text = AiSettings.ClaudeKey;
            cClaudeModel.Text = AiSettings.ClaudeModel;
            tUrl.Text = AiSettings.OpenAiUrl; tKey.Text = AiSettings.OpenAiKey; tModel.Text = AiSettings.OpenAiModel;
            chImage.Checked = AiSettings.SendImage;
        }

        void SaveAll()
        {
            AiSettings.Provider = rClaude.Checked ? AiSettings.Claude : rOpen.Checked ? AiSettings.OpenAi : AiSettings.ClaudeCode;
            AiSettings.ClaudeCodeModel = cCodeModel.SelectedIndex == 0 ? "" : cCodeModel.Text.Trim();
            AiSettings.ClaudeKey = tClaudeKey.Text.Trim();
            AiSettings.ClaudeModel = cClaudeModel.Text.Trim().Length > 0 ? cClaudeModel.Text.Trim() : ClaudeProvider.DefaultModel;
            AiSettings.OpenAiUrl = tUrl.Text.Trim(); AiSettings.OpenAiKey = tKey.Text.Trim(); AiSettings.OpenAiModel = tModel.Text.Trim();
            AiSettings.SendImage = chImage.Checked;
            Settings.Save();
        }
    }
}
