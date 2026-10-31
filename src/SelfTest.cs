using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ComfyModelManager
{
    /// <summary>
    /// Testläge: "ComfyUI Model Manager.exe --selftest".
    /// Skapar en påhittad modell-/workflowstruktur under testdata, kör skanning och arkivering
    /// och kontrollerar resultatet. Rör aldrig riktiga modeller.
    /// </summary>
    public static class SelfTest
    {
        private static readonly StringBuilder LogText = new StringBuilder();
        private static int _passed;
        private static int _failed;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        private const int AttachParentProcess = -1;

        public static int Run()
        {
            bool console = false;
            try
            {
                console = AttachConsole(AttachParentProcess);
                if (console)
                {
                    StreamWriter writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                    writer.AutoFlush = true;
                    Console.SetOut(writer);
                }
            }
            catch
            {
                console = false;
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string root = Path.Combine(baseDir, "testdata");
            int exitCode = 1;

            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);

                string models = Path.Combine(root, "models");
                string workflows = Path.Combine(root, "workflows");
                string archive = Path.Combine(root, "archive");

                Write("=== SJÄLVTEST ===");
                Write("Rot: " + root);
                Write("");

                CreateFixture(models, workflows, archive);

                CancellationTokenSource cts = new CancellationTokenSource();
                ScanReport report = FileScan.Scan(models, workflows, archive, null, cts.Token);
                cts.Dispose();

                Expect(report.Models.Count == 14, string.Format("antal poster = 14 (fick {0})", report.Models.Count));
                Expect(report.WorkflowFiles.Count == 10, string.Format("antal workflows = 10 (fick {0})", report.WorkflowFiles.Count));
                Expect(report.Errors.Count == 0, string.Format("inga läsfel (fick {0})", report.Errors.Count));

                Dictionary<string, ModelFile> byPath = new Dictionary<string, ModelFile>(StringComparer.OrdinalIgnoreCase);
                foreach (ModelFile model in report.Models) byPath[model.RelativePath] = model;

                CheckUsed(byPath, "a.safetensors", true, "skiftläge i ändelsen (A.SafeTensors)");
                CheckUsed(byPath, "sub\\b.gguf", true, "gguf via sökväg i workflow");
                CheckUsed(byPath, "sub\\c.safetensors", false, "oanvänd fil utan träff");
                CheckUsed(byPath, "my model.safetensors", true, "URL-kodat namn (%20)");
                CheckUsed(byPath, "räv.safetensors", true, "unicode-escape (\\u00e4)");
                CheckUsed(byPath, "CaseTest.safetensors", true, "skiftläge i filnamn");
                CheckUsed(byPath, "deep\\x1\\x2\\d.safetensors", false, "oanvänd i djup undermapp");
                CheckUsed(byPath, "e.safetensors", true, "namn omnämnt i löptext/trasig json");
                CheckUsed(byPath, "dup\\dupname.safetensors", true, "dubblett i första mappen");
                CheckUsed(byPath, "dup2\\dupname.safetensors", true, "dubblett i andra mappen");
                CheckUsed(byPath, "old\\collision.safetensors", true, "arkivkollision matchas mot workflow");
                CheckUsed(byPath, "old\\archivedmodel.safetensors", true, "arkiverad använd fil listas");
                CheckUsed(byPath, "old\\foldermodel.safetensors", true, "arkiverad fil för mappval listas");

                ModelFile archivedModel;
                if (byPath.TryGetValue("old\\archivedmodel.safetensors", out archivedModel))
                    Expect(archivedModel.IsArchived, "arkiverad flagga är satt");
                else
                    Expect(false, "arkiverad flagga är satt (filen saknas)");

                Expect(!byPath.ContainsKey("old\\forgotten.safetensors"), "oanvänd arkivfil listas inte");

                int cCount = 0;
                bool cIsArchived = false;
                foreach (ModelFile model in report.Models)
                {
                    if (!string.Equals(model.FileName, "c.safetensors", StringComparison.OrdinalIgnoreCase)) continue;
                    cCount++;
                    if (model.IsArchived) cIsArchived = true;
                }
                Expect(cCount == 1 && !cIsArchived, "oanvänd arkivkopia av c listas inte");

                ModelFile a;
                if (byPath.TryGetValue("a.safetensors", out a))
                    Expect(a.MatchedWorkflows.Count == 1 && Path.GetFileName(a.MatchedWorkflows[0]) == "basic.json",
                        "workflow-listan pekar ut rätt fil");
                else
                    Expect(false, "workflow-listan pekar ut rätt fil (a.safetensors saknas)");

                Expect(!report.Models[0].MatchedWorkflows.Contains(""), "tomma träffar förekommer inte");

                // -- ini
                string iniPath = Path.Combine(root, "test.ini");
                IniFile ini = new IniFile(iniPath);
                ini.Set("Folders", "Models", @"D:\AI modeller\räv");
                ini.Set("Folders", "Workflows", @"C:\arbets flöden\json");
                ini.Set("Folders", "Archive", @"E:\arkiv");
                ini.Save();

                IniFile loaded = new IniFile(iniPath);
                Expect(loaded.Get("Folders", "Models", "") == @"D:\AI modeller\räv", "ini: modellmapp med svenska tecken");
                Expect(loaded.Get("Folders", "Workflows", "") == @"C:\arbets flöden\json", "ini: workflowmapp med mellanslag");
                Expect(loaded.Get("Folders", "Archive", "") == @"E:\arkiv", "ini: arkivmapp");

                // -- arkivering
                List<ModelFile> unused = new List<ModelFile>();
                foreach (ModelFile model in report.Models)
                    if (!model.Used) unused.Add(model);

                Expect(unused.Count == 2, string.Format("två oanvända filer att arkivera (fick {0})", unused.Count));

                ArchiveReport archived = FileScan.Archive(unused, models, archive, null, CancellationToken.None);
                Expect(archived.Moved.Count == 2, string.Format("två filer flyttade (fick {0})", archived.Moved.Count));
                Expect(archived.Failures.Count == 0, string.Format("inga flyttfel (fick {0})", archived.Failures.Count));

                Expect(File.Exists(Path.Combine(archive, "sub", "c (1).safetensors")), "namnkollision fick nytt namn");
                Expect(File.Exists(Path.Combine(archive, "deep", "x1", "x2", "d.safetensors")), "mappstruktur bevarad");
                Expect(!File.Exists(Path.Combine(models, "sub", "c.safetensors")), "källfilen är borta efter flytt");
                Expect(!File.Exists(Path.Combine(models, "deep", "x1", "x2", "d.safetensors")), "djup källfil är borta");
                Expect(File.Exists(Path.Combine(models, "a.safetensors")), "använd fil ligger kvar");

                if (File.Exists(Path.Combine(archive, "sub", "c.safetensors")))
                    Expect(File.ReadAllText(Path.Combine(archive, "sub", "c.safetensors")) == "already archived",
                        "befintlig arkivfil skrevs inte över");
                else
                    Expect(false, "befintlig arkivfil finns kvar");

                // -- omskanning efter arkivering
                CancellationTokenSource cts2 = new CancellationTokenSource();
                ScanReport after = FileScan.Scan(models, workflows, archive, null, cts2.Token);
                cts2.Dispose();

                Expect(after.Models.Count == 12, string.Format("12 poster efter arkivering (fick {0})", after.Models.Count));

                bool allUsed = true;
                foreach (ModelFile model in after.Models) if (!model.Used) allUsed = false;
                Expect(allUsed, "alla kvarvarande poster är använda");

                // -- återställning
                ModelFile restorable = null;
                ModelFile folderModel = null;
                ModelFile collisionModel = null;
                foreach (ModelFile model in after.Models)
                {
                    if (!model.IsArchived) continue;
                    if (model.FileName == "archivedmodel.safetensors") restorable = model;
                    else if (model.FileName == "foldermodel.safetensors") folderModel = model;
                    else if (model.FileName == "collision.safetensors") collisionModel = model;
                }

                Expect(restorable != null && folderModel != null && collisionModel != null,
                    "tre arkiverade filer hittades för återställning");

                RestoreRequest structureRequest = new RestoreRequest();
                structureRequest.File = restorable;
                structureRequest.KeepStructure = true;

                ArchiveReport restoredStructure = FileScan.Restore(
                    new RestoreRequest[] { structureRequest }, models, archive, null, CancellationToken.None);
                Expect(restoredStructure.Moved.Count == 1 && restoredStructure.Failures.Count == 0,
                    "återställning med mappstruktur");
                Expect(File.Exists(Path.Combine(models, "old", "archivedmodel.safetensors")),
                    "strukturen bevarades vid återställning");
                Expect(!File.Exists(Path.Combine(archive, "old", "archivedmodel.safetensors")),
                    "arkivfilen är borta efter återställning");

                string outside = Path.Combine(root, "outside");
                Directory.CreateDirectory(outside);
                Expect(!FileScan.IsValidRestoreFolder(outside, models), "mapp utanför modellmappen är ogiltig");
                Expect(FileScan.IsValidRestoreFolder(models, models), "modellmappen är giltig som mål");
                Expect(FileScan.IsValidRestoreFolder(Path.Combine(models, "sub"), models), "undermapp är giltig som mål");

                RestoreRequest outsideRequest = new RestoreRequest();
                outsideRequest.File = folderModel;
                outsideRequest.KeepStructure = false;
                outsideRequest.TargetFolder = outside;

                ArchiveReport rejected = FileScan.Restore(
                    new RestoreRequest[] { outsideRequest }, models, archive, null, CancellationToken.None);
                Expect(rejected.Moved.Count == 0 && rejected.Failures.Count == 1, "mål utanför modellmappen avvisas");
                Expect(File.Exists(Path.Combine(archive, "old", "foldermodel.safetensors")),
                    "arkivfilen ligger kvar efter avvisat mål");

                string restoreFolder = Path.Combine(models, "restored");
                Directory.CreateDirectory(restoreFolder);

                RestoreRequest folderRequest = new RestoreRequest();
                folderRequest.File = folderModel;
                folderRequest.KeepStructure = false;
                folderRequest.TargetFolder = restoreFolder;

                ArchiveReport restoredFolder = FileScan.Restore(
                    new RestoreRequest[] { folderRequest }, models, archive, null, CancellationToken.None);
                Expect(restoredFolder.Moved.Count == 1 && restoredFolder.Failures.Count == 0,
                    "återställning till vald mapp");
                Expect(File.Exists(Path.Combine(restoreFolder, "foldermodel.safetensors")),
                    "filen hamnade platt i vald mapp");
                Expect(!File.Exists(Path.Combine(archive, "old", "foldermodel.safetensors")),
                    "arkivfilen är borta efter mappval");

                RestoreRequest collisionRequest = new RestoreRequest();
                collisionRequest.File = collisionModel;
                collisionRequest.KeepStructure = true;

                ArchiveReport collision = FileScan.Restore(
                    new RestoreRequest[] { collisionRequest }, models, archive, null, CancellationToken.None);
                Expect(collision.Moved.Count == 0 && collision.Failures.Count == 1, "kollision hoppas över");
                Expect(File.Exists(Path.Combine(archive, "old", "collision.safetensors")),
                    "kollisionsfilen ligger kvar i arkivet");
                Expect(File.Exists(Path.Combine(models, "old", "collision.safetensors")),
                    "befintlig modellfil skrevs inte över");

                // -- bypassed/muted/notes/subgrafer
                RunWorkflowFilterTests(root);

                // -- dubbletter
                RunDuplicateTests(root);

                // -- språk
                RunLanguageTests();

                exitCode = _failed == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Write("OVÄNTAT FEL: " + ex);
                _failed++;
                exitCode = 1;
            }

            Write("");
            Write(string.Format("=== {0} godkända, {1} misslyckade ===", _passed, _failed));

            try
            {
                File.WriteAllText(Path.Combine(baseDir, "selftest.log"), LogText.ToString(), new UTF8Encoding(false));
            }
            catch
            {
                // loggen är bara en bekvämlighet
            }

            if (!console && _failed > 0)
            {
                System.Windows.Forms.MessageBox.Show(LogText.ToString(), "Självtest misslyckades",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            }

            return exitCode;
        }

        private static void RunWorkflowFilterTests(string root)
        {
            string filterRoot = Path.Combine(root, "filter");
            CreateFilterFixture(filterRoot);

            string models = Path.Combine(filterRoot, "models");
            string workflows = Path.Combine(filterRoot, "workflows");
            string archive = Path.Combine(filterRoot, "archive");

            CancellationTokenSource cts = new CancellationTokenSource();
            ScanReport report = FileScan.Scan(models, workflows, archive, null, cts.Token);
            cts.Dispose();

            Expect(report.Models.Count == 20, string.Format("filter: 20 poster (fick {0})", report.Models.Count));
            Expect(report.WorkflowFiles.Count == 10, string.Format("filter: 10 workflows (fick {0})", report.WorkflowFiles.Count));
            Expect(report.Errors.Count == 0, string.Format("filter: inga läsfel (fick {0})", report.Errors.Count));

            Dictionary<string, ModelFile> byPath = new Dictionary<string, ModelFile>(StringComparer.OrdinalIgnoreCase);
            foreach (ModelFile model in report.Models) byPath[model.RelativePath] = model;

            CheckUsed(byPath, "active.safetensors", true, "aktiv nod räknas");
            CheckUsed(byPath, "bypassed.safetensors", false, "bypassed nod räknas inte");
            CheckUsed(byPath, "muted.safetensors", false, "muted nod räknas inte");
            CheckUsed(byPath, "noteonly.safetensors", false, "anteckning räknas inte");
            CheckUsed(byPath, "bothplaces.safetensors", true, "aktiv + bypassed nod räknas som använd");
            CheckUsed(byPath, "subactive.safetensors", true, "aktiv subgraf räknas");
            CheckUsed(byPath, "subbypass.safetensors", false, "bypassed subgraf räknas inte");
            CheckUsed(byPath, "subboth.safetensors", true, "subgraf via aktiv instans räknas");
            CheckUsed(byPath, "innerbypass.safetensors", false, "bypassed nod i aktiv subgraf räknas inte");
            CheckUsed(byPath, "api.safetensors", true, "API-format fallback");
            CheckUsed(byPath, "brokenref.safetensors", true, "trasig json fallback");
            CheckUsed(byPath, "legacygroup.safetensors", true, "gruppnod fallback");
            CheckUsed(byPath, "nested-active.safetensors", true, "nästlad subgraf räknas");
            CheckUsed(byPath, "nested-bypassed.safetensors", false, "bypassed nästlad subgraf räknas inte");
            CheckUsed(byPath, "promo-effective.safetensors", true, "promoted widget-värde på aktiv instans räknas");
            CheckUsed(byPath, "promo-stale.safetensors", false, "subgrafens standardvärde för länkad ingång räknas inte");
            CheckUsed(byPath, "promo-default.safetensors", true, "standardvärde räknas när instansen inte skickar något");
            CheckUsed(byPath, "linked-effective.safetensors", true, "värde från aktiv källa räknas");
            CheckUsed(byPath, "linked-stale.safetensors", false, "sparat värde för länkad ingång räknas inte");
            CheckUsed(byPath, "archived-active.safetensors", true, "arkiverad aktiv referens listas");

            ModelFile archivedActive;
            if (byPath.TryGetValue("archived-active.safetensors", out archivedActive))
                Expect(archivedActive.IsArchived, "arkiverad aktiv referens har arkivflagga");
            else
                Expect(false, "arkiverad aktiv referens har arkivflagga (saknas)");

            Expect(!byPath.ContainsKey("archived-bypassed.safetensors"), "bypassed arkivfil listas inte");
        }

        private static void CreateFilterFixture(string root)
        {
            string models = Path.Combine(root, "models");
            string workflows = Path.Combine(root, "workflows");
            string archive = Path.Combine(root, "archive");

            string[] modelNames = new string[]
            {
                "active", "bypassed", "muted", "noteonly", "bothplaces", "subactive", "subbypass",
                "subboth", "innerbypass", "api", "brokenref", "legacygroup", "nested-active", "nested-bypassed",
                "promo-effective", "promo-stale", "promo-default", "linked-effective", "linked-stale"
            };
            foreach (string name in modelNames)
                WriteFile(Path.Combine(models, name + ".safetensors"), "filter-model");

            WriteFile(Path.Combine(archive, "archived-active.safetensors"), "filter-archived-active");
            WriteFile(Path.Combine(archive, "archived-bypassed.safetensors"), "filter-archived-bypassed");

            WriteFile(Path.Combine(workflows, "ui-modes.json"), @"{
  ""nodes"": [
    {""id"":1,""type"":""UNETLoader"",""mode"":0,""widgets_values"":[""active.safetensors""]},
    {""id"":2,""type"":""UNETLoader"",""mode"":4,""widgets_values"":[""bypassed.safetensors""]},
    {""id"":3,""type"":""UNETLoader"",""mode"":2,""widgets_values"":[""muted.safetensors""]},
    {""id"":4,""type"":""MarkdownNote"",""mode"":0,""widgets_values"":[""noteonly.safetensors""]},
    {""id"":5,""type"":""UNETLoader"",""mode"":0,""widgets_values"":[""bothplaces.safetensors""]},
    {""id"":6,""type"":""LoraLoader"",""mode"":4,""widgets_values"":[""bothplaces.safetensors""]}
  ]
}");

            WriteFile(Path.Combine(workflows, "subgraphs.json"), @"{
  ""nodes"": [
    {""id"":10,""type"":""aaaa-1111"",""mode"":0},
    {""id"":11,""type"":""bbbb-2222"",""mode"":4,""widgets_values"":[""archived-bypassed.safetensors""]},
    {""id"":12,""type"":""cccc-3333"",""mode"":0},
    {""id"":13,""type"":""cccc-3333"",""mode"":4},
    {""id"":14,""type"":""UNETLoader"",""mode"":0,""widgets_values"":[""archived-active.safetensors""]}
  ],
  ""definitions"": {""subgraphs"": [
    {""id"":""aaaa-1111"",""name"":""A"",""nodes"":[
      {""id"":1,""type"":""UNETLoader"",""mode"":0,""widgets_values"":[""subactive.safetensors""]},
      {""id"":4,""type"":""UNETLoader"",""mode"":4,""widgets_values"":[""innerbypass.safetensors""]}
    ]},
    {""id"":""bbbb-2222"",""name"":""B"",""nodes"":[
      {""id"":2,""type"":""UNETLoader"",""mode"":0,""widgets_values"":[""subbypass.safetensors""]}
    ]},
    {""id"":""cccc-3333"",""name"":""C"",""nodes"":[
      {""id"":3,""type"":""UNETLoader"",""mode"":0,""widgets_values"":[""subboth.safetensors""]}
    ]}
  ]}
}");

            WriteFile(Path.Combine(workflows, "api.json"),
                @"{""1"":{""class_type"":""UNETLoader"",""inputs"":{""unet_name"":""api.safetensors""}}}");
            WriteFile(Path.Combine(workflows, "broken.json"),
                "detta är inte json men brokenref.safetensors nämns");
            WriteFile(Path.Combine(workflows, "legacy.json"), @"{
  ""nodes"": [{""id"":1,""type"":""workflow>MinGrupp"",""mode"":0}],
  ""extra"": {""groupNodes"": {""MinGrupp"": {""nodes"": [
      {""id"":2,""type"":""UNETLoader"",""mode"":0,""widgets_values"":[""legacygroup.safetensors""]}
  ]}}}
}");

            WriteFile(Path.Combine(workflows, "nested.json"), @"{
  ""nodes"": [{""id"":1,""type"":""outer-1"",""mode"":0}],
  ""definitions"": {""subgraphs"": [
    {""id"":""outer-1"",""nodes"":[{""id"":2,""type"":""inner-1"",""mode"":0}]},
    {""id"":""inner-1"",""nodes"":[{""id"":3,""type"":""UNETLoader"",""mode"":0,""widgets_values"":[""nested-active.safetensors""]}]}
  ]}
}");

            WriteFile(Path.Combine(workflows, "nested-bypassed.json"), @"{
  ""nodes"": [{""id"":1,""type"":""outer-2"",""mode"":4}],
  ""definitions"": {""subgraphs"": [
    {""id"":""outer-2"",""nodes"":[{""id"":2,""type"":""inner-2"",""mode"":0}]},
    {""id"":""inner-2"",""nodes"":[{""id"":3,""type"":""UNETLoader"",""mode"":0,""widgets_values"":[""nested-bypassed.safetensors""]}]}
  ]}
}");

            WriteFile(Path.Combine(workflows, "promo.json"), @"{
  ""nodes"": [
    {""id"":1,""type"":""promo-def"",""mode"":0,
     ""widgets_values"":[""promo-effective.safetensors""],
     ""widgets_values_named"":{""ckpt_name"":""promo-effective.safetensors""}}
  ],
  ""definitions"": {""subgraphs"": [
    {""id"":""promo-def"",""name"":""Promo"",
     ""inputs"":[{""name"":""ckpt_name"",""type"":""COMBO"",""linkIds"":[81]}],
     ""links"":[{""id"":81,""origin_id"":-10,""origin_slot"":0,""target_id"":2,""target_slot"":0,""type"":""COMBO""}],
     ""nodes"":[
      {""id"":2,""type"":""CheckpointLoaderSimple"",""mode"":0,
       ""inputs"":[{""name"":""ckpt_name"",""type"":""COMBO"",""widget"":{""name"":""ckpt_name""},""link"":81}],
       ""widgets_values"":[""promo-stale.safetensors""],
       ""widgets_values_named"":{""ckpt_name"":""promo-stale.safetensors""}}
    ]}
  ]}
}");

            WriteFile(Path.Combine(workflows, "promo-default.json"), @"{
  ""nodes"": [{""id"":1,""type"":""default-def"",""mode"":0}],
  ""definitions"": {""subgraphs"": [
    {""id"":""default-def"",""name"":""Default"",
     ""inputs"":[{""name"":""unet_name"",""type"":""COMBO"",""linkIds"":[5]}],
     ""links"":[{""id"":5,""origin_id"":-10,""origin_slot"":0,""target_id"":2,""target_slot"":0,""type"":""COMBO""}],
     ""nodes"":[
      {""id"":2,""type"":""UNETLoader"",""mode"":0,
       ""inputs"":[{""name"":""unet_name"",""type"":""COMBO"",""widget"":{""name"":""unet_name""},""link"":5}],
       ""widgets_values"":[""promo-default.safetensors""]}
    ]}
  ]}
}");

            WriteFile(Path.Combine(workflows, "linked.json"), @"{
  ""nodes"": [
    {""id"":1,""type"":""PrimitiveNode"",""mode"":0,""widgets_values"":[""linked-effective.safetensors""]},
    {""id"":2,""type"":""UNETLoader"",""mode"":0,
     ""inputs"":[{""name"":""unet_name"",""type"":""COMBO"",""widget"":{""name"":""unet_name""},""link"":7}],
     ""widgets_values"":[""linked-stale.safetensors""]}
  ],
  ""links"": [{""id"":7,""origin_id"":1,""origin_slot"":0,""target_id"":2,""target_slot"":0,""type"":""COMBO""}]
}");
        }

        private static void RunDuplicateTests(string root)
        {
            string duplicateRoot = Path.Combine(root, "duplicates");
            CreateDuplicateFixture(duplicateRoot);

            string models = Path.Combine(duplicateRoot, "models");
            string workflows = Path.Combine(duplicateRoot, "workflows");
            string archive = Path.Combine(duplicateRoot, "archive");

            CancellationTokenSource cts = new CancellationTokenSource();
            ScanReport report = FileScan.Scan(models, workflows, archive, null, cts.Token);
            cts.Dispose();

            Expect(report.Models.Count == 12, string.Format("dubbletter: 12 poster (fick {0})", report.Models.Count));
            Expect(report.WorkflowFiles.Count == 1, string.Format("dubbletter: 1 workflow (fick {0})", report.WorkflowFiles.Count));
            Expect(report.Errors.Count == 0, string.Format("dubbletter: inga läsfel (fick {0})", report.Errors.Count));

            ModelFile dup = FindArchived(report, "same\\dup.safetensors");
            ModelFile usedDup = FindArchived(report, "same\\useddup.safetensors");
            ModelFile otherName = FindArchived(report, "elsewhere\\name.safetensors");
            ModelFile diffSize = FindArchived(report, "size\\diff.safetensors");
            ModelFile recheck = FindArchived(report, "size\\recheck.safetensors");
            ModelFile missing = FindArchived(report, "size\\missing.safetensors");

            Expect(dup != null && dup.IsDuplicate && !dup.Used, "oanvänd dubblett listas och flaggas");
            Expect(dup != null && string.Equals(dup.DuplicateOfRelative, "same\\dup.safetensors", StringComparison.OrdinalIgnoreCase),
                "dubbletten pekar ut rätt modellkopia");
            Expect(usedDup != null && usedDup.IsDuplicate && usedDup.Used, "använd dubblett flaggas");
            Expect(otherName != null && !otherName.IsDuplicate && otherName.Used, "samma namn i annan mapp är inte dubblett");
            Expect(diffSize != null && !diffSize.IsDuplicate && diffSize.Used, "annan storlek är inte dubblett");
            Expect(recheck != null && recheck.IsDuplicate, "recheck-dubblett flaggas");
            Expect(missing != null && missing.IsDuplicate, "missing-dubblett flaggas");

            int duplicateCount = 0;
            foreach (ModelFile model in report.Models) if (model.IsDuplicate) duplicateCount++;
            Expect(duplicateCount == 4, string.Format("fyra dubbletter hittades (fick {0})", duplicateCount));

            // Ändrad modellkopia ska stoppa radering.
            WriteSizedFile(Path.Combine(models, "size", "recheck.safetensors"), 120);
            DeleteReport recheckReport = FileScan.DeleteDuplicates(
                new ModelFile[] { recheck }, models, archive, null, CancellationToken.None);
            Expect(recheckReport.Deleted.Count == 0 && recheckReport.Failures.Count == 1,
                "ändrad storlek stoppar radering");
            Expect(File.Exists(Path.Combine(archive, "size", "recheck.safetensors")),
                "arkivfilen ligger kvar efter stoppad radering");

            // Försvunnen modellkopia ska stoppa radering.
            File.Delete(Path.Combine(models, "size", "missing.safetensors"));
            DeleteReport missingReport = FileScan.DeleteDuplicates(
                new ModelFile[] { missing }, models, archive, null, CancellationToken.None);
            Expect(missingReport.Deleted.Count == 0 && missingReport.Failures.Count == 1,
                "försvunnen modellkopia stoppar radering");
            Expect(File.Exists(Path.Combine(archive, "size", "missing.safetensors")),
                "arkivfilen ligger kvar när modellkopian saknas");

            // Riktig radering tar bara arkivkopiorna.
            DeleteReport deleted = FileScan.DeleteDuplicates(
                new ModelFile[] { dup, usedDup }, models, archive, null, CancellationToken.None);
            Expect(deleted.Deleted.Count == 2 && deleted.Failures.Count == 0,
                "två dubbletter raderades");
            Expect(deleted.BytesDeleted == 200, string.Format("frigjord storlek = 200 B (fick {0})", deleted.BytesDeleted));
            Expect(!File.Exists(Path.Combine(archive, "same", "dup.safetensors")), "oanvänd dubblett borta");
            Expect(!File.Exists(Path.Combine(archive, "same", "useddup.safetensors")), "använd dubblett borta");
            Expect(File.Exists(Path.Combine(models, "same", "dup.safetensors")), "modellkopian är kvar");
            Expect(File.Exists(Path.Combine(models, "same", "useddup.safetensors")), "använd modellkopia är kvar");

            CancellationTokenSource cts2 = new CancellationTokenSource();
            ScanReport after = FileScan.Scan(models, workflows, archive, null, cts2.Token);
            cts2.Dispose();

            Expect(after.Models.Count == 7, string.Format("dubbletter: 7 poster efter radering (fick {0})", after.Models.Count));
            Expect(FindArchived(after, "same\\dup.safetensors") == null, "raderad dubblett återkommer inte");
        }

        private static ModelFile FindArchived(ScanReport report, string relativePath)
        {
            foreach (ModelFile model in report.Models)
            {
                if (model.IsArchived && string.Equals(model.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
                    return model;
            }
            return null;
        }

        private static void CreateDuplicateFixture(string root)
        {
            string models = Path.Combine(root, "models");
            string workflows = Path.Combine(root, "workflows");
            string archive = Path.Combine(root, "archive");

            WriteSizedFile(Path.Combine(models, "same", "dup.safetensors"), 100);
            WriteSizedFile(Path.Combine(models, "same", "useddup.safetensors"), 100);
            WriteSizedFile(Path.Combine(models, "other", "name.safetensors"), 50);
            WriteSizedFile(Path.Combine(models, "size", "diff.safetensors"), 10);
            WriteSizedFile(Path.Combine(models, "size", "recheck.safetensors"), 100);
            WriteSizedFile(Path.Combine(models, "size", "missing.safetensors"), 100);

            WriteSizedFile(Path.Combine(archive, "same", "dup.safetensors"), 100);
            WriteSizedFile(Path.Combine(archive, "same", "useddup.safetensors"), 100);
            WriteSizedFile(Path.Combine(archive, "elsewhere", "name.safetensors"), 50);
            WriteSizedFile(Path.Combine(archive, "size", "diff.safetensors"), 20);
            WriteSizedFile(Path.Combine(archive, "size", "recheck.safetensors"), 100);
            WriteSizedFile(Path.Combine(archive, "size", "missing.safetensors"), 100);

            WriteFile(Path.Combine(workflows, "wf.json"), @"{""nodes"":[
  {""id"":1,""type"":""UNETLoader"",""mode"":0,""widgets_values"":[""useddup.safetensors""]},
  {""id"":2,""type"":""UNETLoader"",""mode"":0,""widgets_values"":[""name.safetensors""]},
  {""id"":3,""type"":""UNETLoader"",""mode"":0,""widgets_values"":[""diff.safetensors""]}
]}");
        }

        private static void WriteSizedFile(string path, int size)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            byte[] data = new byte[size];
            for (int i = 0; i < size; i++) data[i] = (byte)('a' + (i % 26));
            File.WriteAllBytes(path, data);
        }

        private static void RunLanguageTests()
        {
            Lang.Load("en");
            Expect(Lang.T("button.scan") == "Scan", "språk: engelska är inbyggd");
            Expect(Lang.LanguageExists("en"), "språk: engelska finns");
            Expect(Lang.T("app.license") == "License: MIT", "språk: engelsk licensrad");

            // licensen är inbäddad i exe-filen, så den finns även utan LICENSE-fil
            using (Stream licenseStream = typeof(Lang).Assembly.GetManifestResourceStream("ComfyUI.ModelManager.License.txt"))
            {
                Expect(licenseStream != null, "licens: inbäddad resurs finns");
                if (licenseStream != null)
                {
                    using (StreamReader reader = new StreamReader(licenseStream, Encoding.UTF8, true))
                    {
                        string licenseText = reader.ReadToEnd();
                        Expect(licenseText.IndexOf("MIT License", StringComparison.Ordinal) >= 0 &&
                               licenseText.IndexOf("WITHOUT WARRANTY", StringComparison.Ordinal) >= 0,
                            "licens: MIT-texten är komplett");
                    }
                }
            }

            string[] codes = new string[] { "sv", "de", "fr", "es", "it" };
            for (int i = 0; i < codes.Length; i++)
            {
                string code = codes[i];
                if (!Lang.LanguageExists(code))
                {
                    Write("INFO språkfil saknas: " + code);
                    continue;
                }

                Lang.Load(code);
                Expect(!string.Equals(Lang.T("button.scan"), "Scan", StringComparison.Ordinal),
                    "språk: " + code + " laddas");
                Expect(Lang.F("button.archiveCount", 3).IndexOf("3", StringComparison.Ordinal) >= 0,
                    "språk: " + code + " platshållare");
                Expect(Lang.T("app.license").IndexOf("MIT", StringComparison.Ordinal) >= 0,
                    "språk: " + code + " licensrad");
            }

            if (Lang.LanguageExists("sv"))
            {
                Lang.Load("sv");
                Expect(Lang.CurrentCode == "sv", "språk: vald kod");
                Expect(Lang.Culture.Name == "sv-SE", "språk: kultur från fil");
                Expect(Lang.F("status.scanDone", 1, 1, 1.5).IndexOf("1,5", StringComparison.Ordinal) >= 0,
                    "språk: svenskt decimaltecken");
            }

            Lang.Load("en");
            Expect(Lang.T("button.scan") == "Scan" && Lang.CurrentCode == "en", "språk: engelska återställs");
            Expect(Lang.F("status.scanDone", 1, 1, 1.5).IndexOf("1.5", StringComparison.Ordinal) >= 0,
                "språk: engelskt decimaltecken");

            Lang.Load("sprak-saknas");
            Expect(Lang.CurrentCode == "en" && Lang.T("button.scan") == "Scan",
                "språk: saknad fil faller tillbaka till engelska");

            List<LanguageInfo> languages = Lang.AvailableLanguages();
            Expect(languages.Count >= 1 && languages[0].Code == "en", "språk: engelska är först i listan");

            for (int i = 0; i < codes.Length; i++)
            {
                if (!Lang.LanguageExists(codes[i])) continue;

                bool found = false;
                for (int j = 0; j < languages.Count; j++)
                    if (string.Equals(languages[j].Code, codes[i], StringComparison.OrdinalIgnoreCase)) found = true;

                Expect(found, "språk: " + codes[i] + " finns i språklistan");
            }

            Lang.Load("en");
        }

        private static void CreateFixture(string models, string workflows, string archive)
        {
            // modeller
            WriteFile(Path.Combine(models, "a.safetensors"), "model-a");
            WriteFile(Path.Combine(models, "sub", "b.gguf"), "model-b");
            WriteFile(Path.Combine(models, "sub", "c.safetensors"), "model-c");
            WriteFile(Path.Combine(models, "my model.safetensors"), "model-space");
            WriteFile(Path.Combine(models, "räv.safetensors"), "model-rav");
            WriteFile(Path.Combine(models, "CaseTest.safetensors"), "model-case");
            WriteFile(Path.Combine(models, "deep", "x1", "x2", "d.safetensors"), "model-d");
            WriteFile(Path.Combine(models, "e.safetensors"), "model-e");
            WriteFile(Path.Combine(models, "dup", "dupname.safetensors"), "dup-1");
            WriteFile(Path.Combine(models, "dup2", "dupname.safetensors"), "dup-2");
            WriteFile(Path.Combine(models, "old", "collision.safetensors"), "collision-model");
            WriteFile(Path.Combine(models, "notes", "readme.txt"), "ignorera mig");

            // redan arkiverad fil med samma relativa sökväg som sub\c.safetensors
            WriteFile(Path.Combine(archive, "sub", "c.safetensors"), "already archived");
            WriteFile(Path.Combine(archive, "old", "archivedmodel.safetensors"), "archived-model");
            WriteFile(Path.Combine(archive, "old", "foldermodel.safetensors"), "folder-model");
            WriteFile(Path.Combine(archive, "old", "forgotten.safetensors"), "forgotten");
            WriteFile(Path.Combine(archive, "old", "collision.safetensors"), "collision-archived");

            // workflows
            WriteFile(Path.Combine(workflows, "basic.json"), "{\"1\":{\"inputs\":{\"ckpt_name\":\"A.SafeTensors\"}}}");
            WriteFile(Path.Combine(workflows, "nested", "gguf.json"), "{\"ckpt\":\"sub\\\\b.gguf\"}");
            WriteFile(Path.Combine(workflows, "encoded.json"), "{\"ckpt\":\"my%20model.safetensors\"}");
            WriteFile(Path.Combine(workflows, "escaped.json"), "{\"ckpt\":\"r\\u00e4v.safetensors\"}");
            WriteFile(Path.Combine(workflows, "case.json"), "{\"ckpt\":\"casetest.safetensors\"}");
            WriteFile(Path.Combine(workflows, "broken.json"), "detta är inte json, men e.safetensors nämns här");
            WriteFile(Path.Combine(workflows, "dup.json"), "{\"ckpt\":\"dupname.safetensors\"}");
            WriteFile(Path.Combine(workflows, "archived.json"), "{\"ckpt\":\"archivedmodel.safetensors\"}");
            WriteFile(Path.Combine(workflows, "foldermodel.json"), "{\"ckpt\":\"foldermodel.safetensors\"}");
            WriteFile(Path.Combine(workflows, "collision.json"), "{\"ckpt\":\"collision.safetensors\"}");
            WriteFile(Path.Combine(workflows, "ignored.txt"), "c.safetensors");
        }

        private static void CheckUsed(Dictionary<string, ModelFile> byPath, string relativePath, bool expected, string description)
        {
            ModelFile model;
            if (!byPath.TryGetValue(relativePath, out model))
            {
                Expect(false, description + " (" + relativePath + " saknas)");
                return;
            }
            Expect(model.Used == expected, description + " → " + (model.Used ? "använd" : "oanvänd"));
        }

        private static void WriteFile(string path, string content)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        private static void Expect(bool condition, string description)
        {
            if (condition)
            {
                _passed++;
                Write("OK   " + description);
            }
            else
            {
                _failed++;
                Write("FAIL " + description);
            }
        }

        private static void Write(string line)
        {
            LogText.AppendLine(line);
            try { Console.WriteLine(line); }
            catch { }
        }
    }
}
