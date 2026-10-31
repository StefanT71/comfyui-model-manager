using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyModelManager
{
    public class ModelFile
    {
        public string FullPath;
        public string RelativePath;
        public string FileName;
        public long Size;
        public bool IsArchived;
        public bool IsDuplicate;
        public string DuplicateOf;
        public string DuplicateOfRelative;
        public List<string> MatchedWorkflows = new List<string>();

        public bool Used { get { return MatchedWorkflows.Count > 0; } }
    }

    public class ScanProgress
    {
        public int Percent;
        public string Message;
    }

    public class ScanReport
    {
        public string ModelsRoot = "";
        public string WorkflowsRoot = "";
        public List<ModelFile> Models = new List<ModelFile>();
        public List<string> WorkflowFiles = new List<string>();
        public List<string> Errors = new List<string>();
        public TimeSpan Duration;
    }

    public class ArchiveProgress
    {
        public int Percent;
        public string Message;
    }

    public class ArchiveReport
    {
        public List<string> Moved = new List<string>();
        public List<string> Failures = new List<string>();
        public long BytesMoved;
    }

    public class DeleteReport
    {
        public List<string> Deleted = new List<string>();
        public List<string> Failures = new List<string>();
        public long BytesDeleted;
    }

    /// <summary>
    /// Ett återställningsval för en arkiverad fil.
    /// </summary>
    public class RestoreRequest
    {
        public ModelFile File;
        public bool KeepStructure;
        public string TargetFolder;
    }

    /// <summary>
    /// Långa sökvägar: prefixar med \\?\ när sökvägen är lång.
    /// </summary>
    public static class Paths
    {
        public static string Long(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.Length < 240) return path;
            if (path.StartsWith(@"\\?\")) return path;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path.Substring(2);
            return @"\\?\" + path;
        }

        public static string Strip(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path.Substring(8);
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path.Substring(4);
            return path;
        }
    }

    public static class FileScan
    {
        public static readonly string[] ModelExtensions = new string[] { ".safetensors", ".gguf" };
        public const string WorkflowExtension = ".json";

        private const int MaxNameLength = 260;

        public static bool IsModelFile(string path)
        {
            string ext = Path.GetExtension(path);
            for (int i = 0; i < ModelExtensions.Length; i++)
                if (string.Equals(ext, ModelExtensions[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool IsWorkflowFile(string path)
        {
            return string.Equals(Path.GetExtension(path), WorkflowExtension, StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------- scanning

        public static ScanReport Scan(string modelsRoot, string workflowsRoot, string archiveRoot, IProgress<ScanProgress> progress, CancellationToken token)
        {
            DateTime started = DateTime.UtcNow;
            ScanReport report = new ScanReport();
            report.ModelsRoot = modelsRoot;
            report.WorkflowsRoot = workflowsRoot;

            Report(progress, 1, Lang.T("progress.scanModels"));
            List<string> modelPaths = EnumerateFilesSafe(modelsRoot, IsModelFile, report.Errors, token);
            token.ThrowIfCancellationRequested();

            Report(progress, 4, Lang.T("progress.scanArchive"));
            List<string> archivedPaths = new List<string>();
            if (!string.IsNullOrEmpty(archiveRoot) &&
                Directory.Exists(Paths.Long(archiveRoot)) &&
                !SameRoot(modelsRoot, archiveRoot))
            {
                archivedPaths = EnumerateFilesSafe(archiveRoot, IsModelFile, report.Errors, token);
            }
            token.ThrowIfCancellationRequested();

            Report(progress, 6, Lang.T("progress.scanWorkflows"));
            List<string> workflowPaths = EnumerateFilesSafe(workflowsRoot, IsWorkflowFile, report.Errors, token);
            report.WorkflowFiles = workflowPaths;
            token.ThrowIfCancellationRequested();

            foreach (string path in modelPaths)
                report.Models.Add(CreateModel(path, modelsRoot, false, report));
            foreach (string path in archivedPaths)
                report.Models.Add(CreateModel(path, archiveRoot, true, report));

            MarkDuplicates(report.Models);

            if (workflowPaths.Count == 0)
            {
                // Utan workflows kan ingen arkiverad fil vara i bruk.
                report.Models.RemoveAll(delegate(ModelFile model)
                { return model.IsArchived && !model.Used && !model.IsDuplicate; });
                Report(progress, 100, Lang.T("progress.noWorkflows"));
                report.Duration = DateTime.UtcNow - started;
                return report;
            }

            // Index: normaliserat filnamn -> modeller (samma namn kan finnas i flera mappar).
            Dictionary<string, List<ModelFile>> index = new Dictionary<string, List<ModelFile>>(StringComparer.Ordinal);
            foreach (ModelFile model in report.Models)
            {
                string key = NormalizeName(model.FileName);
                if (key.Length == 0) continue;
                List<ModelFile> list;
                if (!index.TryGetValue(key, out list))
                {
                    list = new List<ModelFile>();
                    index.Add(key, list);
                }
                list.Add(model);
            }

            int total = workflowPaths.Count;
            int done = 0;
            object errorGate = new object();
            MatchTextCache matchCache = new MatchTextCache();

            ParallelOptions options = new ParallelOptions();
            options.CancellationToken = token;
            options.MaxDegreeOfParallelism = Math.Max(2, Math.Min(4, Environment.ProcessorCount));

            // Pass 1: snabb kandidatmatchning (filnamn som förekommer i workflowtexten).
            // Endast aktiva noder räknas: bypassed, muted och anteckningar filtreras bort.
            Parallel.ForEach(workflowPaths, options, delegate(string workflowPath)
            {
                string text = null;
                try
                {
                    text = WorkflowFilter.GetActiveText(ReadAllTextSmart(workflowPath));
                    CacheMatchText(matchCache, workflowPath, text);
                }
                catch (Exception ex)
                {
                    lock (errorGate) report.Errors.Add(workflowPath + " : " + ex.Message);
                }

                if (text != null)
                {
                    HashSet<string> candidates = new HashSet<string>(StringComparer.Ordinal);
                    CollectCandidates(text, candidates);

                    string unescaped = UnescapeUnicode(text);
                    if (!ReferenceEquals(unescaped, text)) CollectCandidates(unescaped, candidates);

                    foreach (string candidate in candidates)
                    {
                        List<ModelFile> matches;
                        if (!index.TryGetValue(NormalizeName(candidate), out matches)) continue;
                        for (int i = 0; i < matches.Count; i++) AddMatch(matches[i], workflowPath);
                    }
                }

                int current = Interlocked.Increment(ref done);
                if (current % 20 == 0 || current == total)
                    Report(progress, 10 + (int)(80.0 * current / total), Lang.F("progress.readWorkflows", current, total));
            });

            token.ThrowIfCancellationRequested();

            // Pass 2: kontroll av oanvända modeller med ren textmatchning.
            // Fångar udda fall (t.ex. filnamn inbäddat i löptext) som pass 1 inte hittar.
            List<ModelFile> unmatched = new List<ModelFile>();
            foreach (ModelFile model in report.Models)
                if (model.MatchedWorkflows.Count == 0) unmatched.Add(model);

            if (unmatched.Count > 0)
            {
                done = 0;
                Report(progress, 92, Lang.T("progress.checkUnused"));
                Parallel.ForEach(workflowPaths, options, delegate(string workflowPath)
                {
                    string text = null;
                    try
                    {
                        if (!matchCache.Items.TryGetValue(workflowPath, out text))
                            text = WorkflowFilter.GetActiveText(ReadAllTextSmart(workflowPath));
                    }
                    catch
                    {
                        // redan rapporterat i pass 1
                    }

                    if (text != null)
                    {
                        for (int i = 0; i < unmatched.Count; i++)
                        {
                            ModelFile model = unmatched[i];
                            if (model.MatchedWorkflows.Count > 0) continue;
                            if (ContainsFileName(text, model.FileName)) AddMatch(model, workflowPath);
                        }
                    }

                    int current = Interlocked.Increment(ref done);
                    if (current % 20 == 0 || current == total)
                        Report(progress, 92 + (int)(7.0 * current / total), Lang.F("progress.checking", current, total));
                });
            }

            // Arkiverade filer som inte används ska inte visas. Dubbletter visas alltid.
            report.Models.RemoveAll(delegate(ModelFile model)
            { return model.IsArchived && !model.Used && !model.IsDuplicate; });

            foreach (ModelFile model in report.Models)
                model.MatchedWorkflows.Sort(StringComparer.OrdinalIgnoreCase);

            Report(progress, 100, Lang.T("progress.ready"));
            report.Duration = DateTime.UtcNow - started;
            return report;
        }

        private static void AddMatch(ModelFile model, string workflowPath)
        {
            lock (model.MatchedWorkflows)
            {
                if (!model.MatchedWorkflows.Contains(workflowPath))
                    model.MatchedWorkflows.Add(workflowPath);
            }
        }

        private sealed class MatchTextCache
        {
            public readonly ConcurrentDictionary<string, string> Items =
                new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public long Chars;
        }

        private const long MaxCachedChars = 64L * 1024 * 1024;

        private static void CacheMatchText(MatchTextCache cache, string workflowPath, string text)
        {
            if (cache == null || text == null || text.Length == 0) return;
            if (text.Length > 4 * 1024 * 1024) return;

            long total = Interlocked.Add(ref cache.Chars, text.Length);
            if (total > MaxCachedChars)
            {
                Interlocked.Add(ref cache.Chars, -text.Length);
                return;
            }
            cache.Items[workflowPath] = text;
        }

        /// <summary>
        /// Plockar ut alla filnamnsliknande strängar som slutar på .safetensors eller .gguf.
        /// </summary>
        private static void CollectCandidates(string text, HashSet<string> into)
        {
            int pos = 0;
            int length = text.Length;
            while (pos < length)
            {
                int i1 = text.IndexOf(".safetensors", pos, StringComparison.OrdinalIgnoreCase);
                int i2 = text.IndexOf(".gguf", pos, StringComparison.OrdinalIgnoreCase);
                if (i1 < 0 && i2 < 0) break;

                int index;
                int extLength;
                if (i1 >= 0 && (i2 < 0 || i1 <= i2))
                {
                    index = i1;
                    extLength = 12;
                }
                else
                {
                    index = i2;
                    extLength = 5;
                }

                int end = index + extLength;
                int start = index;
                while (start > 0)
                {
                    char c = text[start - 1];
                    if (c == '"' || c == '\'' || c == '\\' || c == '/' || c == '\r' || c == '\n' || c == '\t' ||
                        c == '{' || c == '}' || c == '[' || c == ']' || c == ',' || c == ':' || c == ';' ||
                        c == '|' || c == '<' || c == '>' || c == '=' || c == '(' || c == ')')
                        break;
                    start--;
                    if (index - start > MaxNameLength) break;
                }

                string candidate = text.Substring(start, end - start).Trim(' ', '\u00A0');
                if (candidate.Length > 0)
                {
                    into.Add(candidate);
                    int space = candidate.LastIndexOf(' ');
                    if (space > 0 && space < candidate.Length - 1) into.Add(candidate.Substring(space + 1));
                }

                pos = end;
            }
        }

        private static bool ContainsFileName(string text, string fileName)
        {
            if (ContainsBounded(text, fileName)) return true;
            foreach (string variant in NameVariants(fileName))
                if (ContainsBounded(text, variant)) return true;
            return false;
        }

        /// <summary>
        /// Som IndexOf, men bara när förekomsten är ett eget filnamn och inte en del
        /// av ett längre namn (t.ex. ska "dup.safetensors" inte matcha "useddup.safetensors").
        /// </summary>
        private static bool ContainsBounded(string text, string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > text.Length) return false;

            int index = 0;
            while (index <= text.Length - value.Length)
            {
                index = text.IndexOf(value, index, StringComparison.OrdinalIgnoreCase);
                if (index < 0) return false;

                bool leftOk = index == 0 || !IsFileNameChar(text[index - 1]);
                int end = index + value.Length;
                bool rightOk = end >= text.Length || !IsFileNameChar(text[end]);
                if (leftOk && rightOk) return true;

                index++;
            }
            return false;
        }

        private static bool IsFileNameChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' || c == '%';
        }

        private static IEnumerable<string> NameVariants(string name)
        {
            if (name.IndexOf(' ') >= 0) yield return name.Replace(" ", "%20");

            string escaped;
            try { escaped = Uri.EscapeDataString(name); }
            catch { escaped = name; }
            if (!string.Equals(escaped, name, StringComparison.Ordinal)) yield return escaped;

            bool hasNonAscii = false;
            for (int i = 0; i < name.Length; i++)
            {
                if (name[i] > 127) { hasNonAscii = true; break; }
            }
            if (hasNonAscii)
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < name.Length; i++)
                {
                    char c = name[i];
                    if (c > 127) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                }
                yield return sb.ToString();
            }
        }

        /// <summary>
        /// Normaliserar ett filnamn för jämförelse: procentavkodning, unicodeavkodning, FormC, gemener.
        /// </summary>
        public static string NormalizeName(string name)
        {
            if (name == null) return "";
            string value = name.Trim();
            if (value.Length == 0) return "";
            if (value.IndexOf('%') >= 0)
            {
                try { value = Uri.UnescapeDataString(value); }
                catch { }
            }
            if (value.IndexOf("\\u", StringComparison.Ordinal) >= 0) value = UnescapeUnicode(value);
            value = value.Normalize(NormalizationForm.FormC);
            return value.ToLowerInvariant();
        }

        /// <summary>
        /// Ersätter \uXXXX-sekvenser med tecknet. Returnerar samma sträng om inget finns.
        /// </summary>
        public static string UnescapeUnicode(string text)
        {
            if (text == null) return null;
            if (text.IndexOf("\\u", StringComparison.Ordinal) < 0) return text;

            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\\' && i + 5 < text.Length && text[i + 1] == 'u')
                {
                    int code;
                    if (int.TryParse(text.Substring(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                    {
                        sb.Append((char)code);
                        i += 5;
                        continue;
                    }
                }
                sb.Append(text[i]);
            }
            return sb.ToString();
        }

        private static void Report(IProgress<ScanProgress> progress, int percent, string message)
        {
            if (progress == null) return;
            ScanProgress p = new ScanProgress();
            p.Percent = percent;
            p.Message = message;
            progress.Report(p);
        }

        // ------------------------------------------------------------- filsystem

        public static List<string> EnumerateFilesSafe(string root, Func<string, bool> match, List<string> errors, CancellationToken token)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(Paths.Long(root))) return result;

            Stack<string> stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string dir = stack.Pop();
                string[] files = null;
                string[] dirs = null;

                try
                {
                    files = Directory.GetFiles(Paths.Long(dir));
                }
                catch (Exception ex)
                {
                    if (errors != null) lock (errors) errors.Add(dir + " : " + ex.Message);
                }

                if (files != null)
                {
                    for (int i = 0; i < files.Length; i++)
                    {
                        string file = Paths.Strip(files[i]);
                        if (match(file)) result.Add(file);
                    }
                }

                try
                {
                    dirs = Directory.GetDirectories(Paths.Long(dir));
                }
                catch (Exception ex)
                {
                    if (errors != null) lock (errors) errors.Add(dir + " : " + ex.Message);
                }

                if (dirs != null)
                {
                    for (int i = 0; i < dirs.Length; i++)
                    {
                        string sub = Paths.Strip(dirs[i]);
                        try
                        {
                            FileAttributes attr = File.GetAttributes(Paths.Long(sub));
                            if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                        }
                        catch
                        {
                            continue;
                        }
                        stack.Push(sub);
                    }
                }
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        public static string ReadAllTextSmart(string path)
        {
            byte[] buffer;
            using (FileStream stream = new FileStream(Paths.Long(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long length = stream.Length;
                if (length > int.MaxValue) throw new IOException(Lang.T("error.fileTooLarge"));
                buffer = new byte[(int)length];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = stream.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                return DecodeText(buffer, read);
            }
        }

        private static string DecodeText(byte[] buffer, int length)
        {
            if (length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                return Encoding.UTF8.GetString(buffer, 3, length - 3);
            if (length >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
                return Encoding.Unicode.GetString(buffer, 2, length - 2);
            if (length >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(buffer, 2, length - 2);

            try
            {
                return new UTF8Encoding(false, true).GetString(buffer, 0, length);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Default.GetString(buffer, 0, length);
            }
        }

        private static string MakeRelative(string root, string fullPath)
        {
            string trimmed = root.TrimEnd('\\', '/');
            if (fullPath.StartsWith(trimmed + "\\", StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(trimmed + "/", StringComparison.OrdinalIgnoreCase))
                return fullPath.Substring(trimmed.Length + 1);
            return fullPath;
        }

        private static ModelFile CreateModel(string path, string root, bool archived, ScanReport report)
        {
            ModelFile model = new ModelFile();
            model.FullPath = path;
            model.FileName = Path.GetFileName(path);
            model.RelativePath = MakeRelative(root, path);
            model.IsArchived = archived;
            try
            {
                FileInfo info = new FileInfo(Paths.Long(path));
                model.Size = info.Length;
            }
            catch (Exception ex)
            {
                model.Size = 0;
                lock (report.Errors) report.Errors.Add(path + " : " + ex.Message);
            }
            return model;
        }

        private static bool SameRoot(string first, string second)
        {
            try
            {
                return string.Equals(Path.GetFullPath(first).TrimEnd('\\', '/'),
                                     Path.GetFullPath(second).TrimEnd('\\', '/'),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Flaggar arkivfiler som har en identisk kopia i modellmappen:
        /// samma relativa sökväg och samma storlek. Endast arkivkopian flaggas.
        /// </summary>
        private static void MarkDuplicates(List<ModelFile> models)
        {
            Dictionary<string, ModelFile> index = new Dictionary<string, ModelFile>(StringComparer.Ordinal);

            foreach (ModelFile model in models)
            {
                if (model.IsArchived) continue;
                string key = DuplicateKey(model);
                if (!index.ContainsKey(key)) index.Add(key, model);
            }

            foreach (ModelFile model in models)
            {
                if (!model.IsArchived) continue;

                ModelFile original;
                if (!index.TryGetValue(DuplicateKey(model), out original)) continue;

                model.IsDuplicate = true;
                model.DuplicateOf = original.FullPath;
                model.DuplicateOfRelative = original.RelativePath;
            }
        }

        private static string DuplicateKey(ModelFile model)
        {
            string path = NormalizeName(model.RelativePath).Replace('/', '\\');
            return path + "|" + model.Size.ToString(CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------ arkivering

        public static ArchiveReport Archive(IEnumerable<ModelFile> files, string modelsRoot, string archiveRoot, IProgress<ArchiveProgress> progress, CancellationToken token)
        {
            ArchiveReport report = new ArchiveReport();
            List<ModelFile> list = new List<ModelFile>();
            foreach (ModelFile file in files) list.Add(file);

            int done = 0;
            foreach (ModelFile model in list)
            {
                token.ThrowIfCancellationRequested();
                done++;

                ReportArchive(progress, list.Count == 0 ? 100 : (int)(100.0 * (done - 1) / list.Count),
                    Lang.F("progress.moving", done, list.Count, model.FileName));

                try
                {
                    string relative = model.RelativePath;
                    if (Path.IsPathRooted(relative))
                        relative = MakeRelative(modelsRoot, model.FullPath);

                    TryMove(model, Path.Combine(archiveRoot, relative), true, report, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    report.Failures.Add(model.FullPath + " : " + ex.Message);
                }
            }

            ReportArchive(progress, 100, Lang.T("progress.archiveDone"));
            return report;
        }

        /// <summary>
        /// Återställer arkiverade filer. Varje fil har ett eget val: behåll mappstruktur eller vald mapp.
        /// </summary>
        public static ArchiveReport Restore(IEnumerable<RestoreRequest> requests, string modelsRoot, string archiveRoot, IProgress<ArchiveProgress> progress, CancellationToken token)
        {
            ArchiveReport report = new ArchiveReport();
            List<RestoreRequest> list = new List<RestoreRequest>();
            foreach (RestoreRequest request in requests) list.Add(request);

            int done = 0;
            foreach (RestoreRequest request in list)
            {
                token.ThrowIfCancellationRequested();
                done++;

                ReportArchive(progress, list.Count == 0 ? 100 : (int)(100.0 * (done - 1) / list.Count),
                    Lang.F("progress.restoring", done, list.Count, request.File == null ? "?" : request.File.FileName));

                try
                {
                    if (request.File == null) throw new InvalidOperationException(Lang.T("error.noFile"));
                    if (!request.File.IsArchived) throw new InvalidOperationException(Lang.T("error.notArchived"));

                    string target = GetRestoreTarget(request.File, modelsRoot, request.KeepStructure, request.TargetFolder);
                    if (!IsUnderRoot(target, modelsRoot)) throw new InvalidOperationException(Lang.T("error.outsideModels"));
                    if (!string.IsNullOrEmpty(archiveRoot) && IsUnderRoot(target, archiveRoot))
                        throw new InvalidOperationException(Lang.T("error.targetInArchive"));

                    TryMove(request.File, target, false, report, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    report.Failures.Add((request.File == null ? "?" : request.File.FullPath) + " : " + ex.Message);
                }
            }

            ReportArchive(progress, 100, Lang.T("progress.restoreDone"));
            return report;
        }

        /// <summary>
        /// Beräknar målvägen för en återställning. Används både för förhandsvisning och vid flytten.
        /// </summary>
        public static string GetRestoreTarget(ModelFile file, string modelsRoot, bool keepStructure, string targetFolder)
        {
            if (file == null) throw new ArgumentNullException("file");
            if (keepStructure) return Path.Combine(modelsRoot, file.RelativePath);
            if (string.IsNullOrEmpty(targetFolder)) throw new InvalidOperationException(Lang.T("error.noTargetFolder"));
            return Path.Combine(targetFolder, file.FileName);
        }

        /// <summary>
        /// Sant om sökvägen ligger under roten (inte roten själv).
        /// </summary>
        public static bool IsUnderRoot(string path, string root)
        {
            try
            {
                string fullRoot = Path.GetFullPath(root).TrimEnd('\\', '/');
                string fullPath = Path.GetFullPath(path);
                return fullPath.StartsWith(fullRoot + "\\", StringComparison.OrdinalIgnoreCase) ||
                       fullPath.StartsWith(fullRoot + "/", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// En giltig återställningsmapp ligger under modellmappen (eller är modellmappen själv) och finns.
        /// </summary>
        public static bool IsValidRestoreFolder(string folder, string modelsRoot)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(Paths.Long(folder))) return false;
            if (SameRoot(folder, modelsRoot)) return true;
            return IsUnderRoot(folder, modelsRoot);
        }

        /// <summary>
        /// Raderar arkivkopior som är dubbletter av en modellkopia. Verifierar
        /// sökväg och storlek igen precis före radering. Modellmappen rörs aldrig.
        /// </summary>
        public static DeleteReport DeleteDuplicates(IEnumerable<ModelFile> files, string modelsRoot, string archiveRoot, IProgress<ArchiveProgress> progress, CancellationToken token)
        {
            DeleteReport report = new DeleteReport();
            List<ModelFile> list = new List<ModelFile>();
            foreach (ModelFile file in files) list.Add(file);

            int done = 0;
            foreach (ModelFile file in list)
            {
                token.ThrowIfCancellationRequested();
                done++;

                ReportArchive(progress, list.Count == 0 ? 100 : (int)(100.0 * (done - 1) / list.Count),
                    Lang.F("progress.deleting", done, list.Count, file.FileName));

                try
                {
                    if (!file.IsArchived || !file.IsDuplicate)
                        throw new InvalidOperationException(Lang.T("error.notDuplicate"));
                    if (Path.IsPathRooted(file.RelativePath))
                        throw new InvalidOperationException(Lang.T("error.invalidRelativePath"));

                    string counterpart = Path.Combine(modelsRoot, file.RelativePath);
                    FileInfo modelInfo = new FileInfo(Paths.Long(counterpart));
                    FileInfo archiveInfo = new FileInfo(Paths.Long(file.FullPath));

                    if (!modelInfo.Exists)
                        throw new IOException(Lang.T("error.modelMissing"));
                    if (!archiveInfo.Exists)
                        throw new IOException(Lang.T("error.archiveMissing"));
                    if (modelInfo.Length != file.Size || archiveInfo.Length != file.Size)
                        throw new IOException(Lang.T("error.sizeMismatch"));

                    File.Delete(Paths.Long(file.FullPath));
                    report.Deleted.Add(file.FullPath);
                    report.BytesDeleted += file.Size;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    report.Failures.Add(file.FullPath + " : " + ex.Message);
                }
            }

            ReportArchive(progress, 100, Lang.T("progress.deleteDone"));
            return report;
        }

        private static void TryMove(ModelFile file, string target, bool uniqueOnCollision, ArchiveReport report, CancellationToken token)
        {
            string targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(Paths.Long(targetDir)))
                Directory.CreateDirectory(Paths.Long(targetDir));

            string finalTarget = target;
            if (uniqueOnCollision)
            {
                finalTarget = UniqueTarget(finalTarget);
            }
            else if (File.Exists(Paths.Long(finalTarget)))
            {
                throw new IOException(Lang.T("error.targetExists"));
            }

            MoveFile(file.FullPath, finalTarget, token, null);
            report.Moved.Add(finalTarget);
            report.BytesMoved += file.Size;
        }

        private static void ReportArchive(IProgress<ArchiveProgress> progress, int percent, string message)
        {
            if (progress == null) return;
            ArchiveProgress p = new ArchiveProgress();
            p.Percent = percent;
            p.Message = message;
            progress.Report(p);
        }

        private static string UniqueTarget(string target)
        {
            if (!File.Exists(Paths.Long(target)) && !Directory.Exists(Paths.Long(target))) return target;

            string dir = Path.GetDirectoryName(target);
            string name = Path.GetFileNameWithoutExtension(target);
            string ext = Path.GetExtension(target);

            for (int i = 1; i < 10000; i++)
            {
                string candidate = Path.Combine(dir, name + " (" + i.ToString(CultureInfo.InvariantCulture) + ")" + ext);
                if (!File.Exists(Paths.Long(candidate)) && !Directory.Exists(Paths.Long(candidate))) return candidate;
            }
            throw new IOException(Lang.F("error.noFreeName", target));
        }

        public static void MoveFile(string source, string target, CancellationToken token, Action<long> bytesProgress)
        {
            token.ThrowIfCancellationRequested();

            string src = Paths.Long(source);
            string dst = Paths.Long(target);

            bool sameVolume = string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
            if (sameVolume)
            {
                File.Move(src, dst);
                return;
            }

            try
            {
                CopyWithProgress(src, dst, token, bytesProgress);
                try
                {
                    File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src));
                }
                catch
                {
                    // tidsstämpel är inte kritiskt
                }
                File.Delete(src);
            }
            catch
            {
                try { if (File.Exists(dst)) File.Delete(dst); }
                catch { }
                throw;
            }
        }

        private static void CopyWithProgress(string source, string target, CancellationToken token, Action<long> bytesProgress)
        {
            byte[] buffer = new byte[1024 * 1024];
            long total = 0;
            using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int read = input.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    output.Write(buffer, 0, read);
                    total += read;
                    if (bytesProgress != null) bytesProgress(total);
                }
                output.Flush(true);
            }
        }
    }
}
