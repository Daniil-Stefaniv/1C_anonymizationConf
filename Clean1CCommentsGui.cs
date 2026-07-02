using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;

namespace Clean1CCommentsGui
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--self-test")
            {
                Cleaner.RunSelfTests();
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }

    internal enum RunMode
    {
        DryRun,
        DryRunWithXmlValidation,
        Apply,
        ApplyWithBackup
    }

    internal sealed class MainForm : Form
    {
        private readonly TextBox folderTextBox;
        private readonly Button browseButton;
        private readonly Button dryRunButton;
        private readonly Button validateDryRunButton;
        private readonly Button applyButton;
        private readonly Button applyBackupButton;
        private readonly Button selfTestButton;
        private readonly Button cancelButton;
        private readonly Button openReportButton;
        private readonly NumericUpDown jobsBox;
        private readonly CheckBox extendedScanCheckBox;
        private readonly ProgressBar progressBar;
        private readonly Label statusLabel;
        private readonly TextBox logTextBox;

        private CancellationTokenSource cancellation;
        private string lastReportPath;

        public MainForm()
        {
            Text = "Очистка комментариев 1С";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(880, 580);
            Size = new Size(1040, 720);
            Font = new Font("Segoe UI", 9F);

            var main = new TableLayoutPanel();
            main.Dock = DockStyle.Fill;
            main.Padding = new Padding(12);
            main.ColumnCount = 1;
            main.RowCount = 6;
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Controls.Add(main);

            var folderPanel = new TableLayoutPanel();
            folderPanel.Dock = DockStyle.Top;
            folderPanel.ColumnCount = 3;
            folderPanel.RowCount = 1;
            folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            main.Controls.Add(folderPanel, 0, 0);

            var folderLabel = new Label();
            folderLabel.Text = "Папка конфигурации:";
            folderLabel.AutoSize = true;
            folderLabel.Anchor = AnchorStyles.Left;
            folderLabel.Margin = new Padding(0, 6, 8, 6);
            folderPanel.Controls.Add(folderLabel, 0, 0);

            folderTextBox = new TextBox();
            folderTextBox.Dock = DockStyle.Fill;
            folderTextBox.Margin = new Padding(0, 3, 8, 3);
            folderTextBox.Text = Environment.CurrentDirectory;
            folderPanel.Controls.Add(folderTextBox, 1, 0);

            browseButton = new Button();
            browseButton.Text = "Выбрать...";
            browseButton.AutoSize = true;
            browseButton.Click += BrowseButton_Click;
            folderPanel.Controls.Add(browseButton, 2, 0);

            var optionsPanel = new FlowLayoutPanel();
            optionsPanel.Dock = DockStyle.Top;
            optionsPanel.AutoSize = true;
            optionsPanel.Margin = new Padding(0, 8, 0, 4);
            main.Controls.Add(optionsPanel, 0, 1);

            optionsPanel.Controls.Add(new Label
            {
                Text = "Потоки:",
                AutoSize = true,
                Margin = new Padding(0, 7, 6, 3)
            });

            jobsBox = new NumericUpDown();
            jobsBox.Minimum = 1;
            jobsBox.Maximum = 64;
            jobsBox.Value = Math.Min(8, Math.Max(1, Environment.ProcessorCount));
            jobsBox.Width = 64;
            optionsPanel.Controls.Add(jobsBox);

            extendedScanCheckBox = new CheckBox();
            extendedScanCheckBox.Text = "дополнительно проверять неизвестные текстовые файлы только при XML-признаках";
            extendedScanCheckBox.AutoSize = true;
            extendedScanCheckBox.Margin = new Padding(18, 5, 0, 3);
            optionsPanel.Controls.Add(extendedScanCheckBox);

            var buttonsPanel = new FlowLayoutPanel();
            buttonsPanel.Dock = DockStyle.Top;
            buttonsPanel.AutoSize = true;
            buttonsPanel.Margin = new Padding(0, 8, 0, 8);
            main.Controls.Add(buttonsPanel, 0, 2);

            dryRunButton = CreateActionButton("Проверить", delegate { StartRun(RunMode.DryRun); });
            validateDryRunButton = CreateActionButton("Проверить + XML-валидация", delegate { StartRun(RunMode.DryRunWithXmlValidation); });
            applyButton = CreateActionButton("Очистить", delegate { StartRun(RunMode.Apply); });
            applyBackupButton = CreateActionButton("Очистить + backup", delegate { StartRun(RunMode.ApplyWithBackup); });
            selfTestButton = CreateActionButton("Самотест", delegate { RunSelfTestFromUi(); });
            cancelButton = CreateActionButton("Остановить", delegate { CancelRun(); });
            cancelButton.Enabled = false;
            openReportButton = CreateActionButton("Открыть отчет", delegate { OpenLastReport(); });
            openReportButton.Enabled = false;

            buttonsPanel.Controls.Add(dryRunButton);
            buttonsPanel.Controls.Add(validateDryRunButton);
            buttonsPanel.Controls.Add(applyButton);
            buttonsPanel.Controls.Add(applyBackupButton);
            buttonsPanel.Controls.Add(selfTestButton);
            buttonsPanel.Controls.Add(cancelButton);
            buttonsPanel.Controls.Add(openReportButton);

            progressBar = new ProgressBar();
            progressBar.Dock = DockStyle.Top;
            progressBar.Height = 22;
            progressBar.Minimum = 0;
            progressBar.Maximum = 100;
            main.Controls.Add(progressBar, 0, 3);

            statusLabel = new Label();
            statusLabel.Text = "Готово.";
            statusLabel.AutoSize = true;
            statusLabel.Margin = new Padding(0, 8, 0, 8);
            main.Controls.Add(statusLabel, 0, 4);

            logTextBox = new TextBox();
            logTextBox.Dock = DockStyle.Fill;
            logTextBox.Multiline = true;
            logTextBox.ScrollBars = ScrollBars.Both;
            logTextBox.ReadOnly = true;
            logTextBox.WordWrap = false;
            logTextBox.Font = new Font("Consolas", 9F);
            main.Controls.Add(logTextBox, 0, 5);
        }

        private static Button CreateActionButton(string text, EventHandler handler)
        {
            var button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.Margin = new Padding(0, 0, 8, 8);
            button.Padding = new Padding(8, 3, 8, 3);
            button.Click += handler;
            return button;
        }

        private void BrowseButton_Click(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Выберите папку выгрузки конфигурации 1С";
                dialog.SelectedPath = Directory.Exists(folderTextBox.Text) ? folderTextBox.Text : Environment.CurrentDirectory;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    folderTextBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void StartRun(RunMode mode)
        {
            var root = folderTextBox.Text.Trim();
            if (!Directory.Exists(root))
            {
                MessageBox.Show(this, "Папка не найдена.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var isApply = mode == RunMode.Apply || mode == RunMode.ApplyWithBackup;
            if (isApply)
            {
                var question = mode == RunMode.ApplyWithBackup
                    ? "Будут изменены файлы конфигурации. Перед изменением будет создан backup. Продолжить?"
                    : "Будут изменены файлы конфигурации без автоматического backup. Продолжить?";
                if (MessageBox.Show(this, question, "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return;
                }
            }

            logTextBox.Clear();
            lastReportPath = null;
            openReportButton.Enabled = false;
            SetRunning(true);
            progressBar.Value = 0;
            statusLabel.Text = "Запуск...";

            cancellation = new CancellationTokenSource();
            var options = new RunOptions();
            options.Root = Path.GetFullPath(root);
            options.DryRun = !isApply;
            options.ValidateDryRun = mode == RunMode.DryRunWithXmlValidation;
            options.CreateBackup = mode == RunMode.ApplyWithBackup;
            options.ExtendedScan = extendedScanCheckBox.Checked;
            options.Jobs = (int)jobsBox.Value;

            AppendLog("Режим: " + ModeTitle(mode));
            AppendLog("Папка: " + options.Root);
            AppendLog("Потоки: " + options.Jobs);
            AppendLog("Расширенная проверка: " + (options.ExtendedScan ? "включена" : "выключена"));

            Task.Factory.StartNew(delegate
            {
                return Cleaner.Run(options, cancellation.Token, delegate(ProgressInfo progress)
                {
                    BeginInvoke(new Action(delegate { UpdateProgress(progress); }));
                });
            }, cancellation.Token).ContinueWith(delegate(Task<RunResult> task)
            {
                BeginInvoke(new Action(delegate
                {
                    SetRunning(false);
                    if (task.IsFaulted)
                    {
                        var ex = task.Exception != null && task.Exception.InnerException != null ? task.Exception.InnerException : task.Exception;
                        statusLabel.Text = "Ошибка.";
                        AppendLog("Ошибка: " + ex.Message);
                        MessageBox.Show(this, ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    if (task.IsCanceled || cancellation.IsCancellationRequested)
                    {
                        statusLabel.Text = "Остановлено.";
                        AppendLog("Операция остановлена пользователем.");
                        return;
                    }
                    ShowResult(task.Result);
                }));
            });
        }

        private static string ModeTitle(RunMode mode)
        {
            switch (mode)
            {
                case RunMode.DryRun:
                    return "проверка без изменений";
                case RunMode.DryRunWithXmlValidation:
                    return "проверка без изменений + XML-валидация";
                case RunMode.Apply:
                    return "очистка";
                case RunMode.ApplyWithBackup:
                    return "очистка + backup";
                default:
                    return mode.ToString();
            }
        }

        private void SetRunning(bool running)
        {
            browseButton.Enabled = !running;
            dryRunButton.Enabled = !running;
            validateDryRunButton.Enabled = !running;
            applyButton.Enabled = !running;
            applyBackupButton.Enabled = !running;
            selfTestButton.Enabled = !running;
            jobsBox.Enabled = !running;
            extendedScanCheckBox.Enabled = !running;
            cancelButton.Enabled = running;
        }

        private void UpdateProgress(ProgressInfo progress)
        {
            if (progress.TotalFiles > 0)
            {
                var percent = (int)Math.Min(100, Math.Max(0, progress.ProcessedFiles * 100L / progress.TotalFiles));
                progressBar.Value = percent;
                statusLabel.Text = string.Format(
                    "Обработано {0}/{1}. Изменяемых файлов: {2}. Ошибок: {3}.",
                    progress.ProcessedFiles,
                    progress.TotalFiles,
                    progress.ChangedFiles,
                    progress.Errors);
            }
            else
            {
                progressBar.Value = 0;
                statusLabel.Text = progress.Message;
            }

            if (!string.IsNullOrEmpty(progress.Message))
            {
                AppendLog(progress.Message);
            }
        }

        private void ShowResult(RunResult result)
        {
            progressBar.Value = 100;
            statusLabel.Text = result.Cancelled ? "Остановлено." : "Готово.";
            lastReportPath = result.ReportPath;
            openReportButton.Enabled = !string.IsNullOrEmpty(lastReportPath) && File.Exists(lastReportPath);

            AppendLog("");
            AppendLog("Итог:");
            AppendLog("Файлов найдено: " + result.Summary.FilesSeen);
            AppendLog("Файлов обработано: " + result.Summary.FilesProcessed);
            AppendLog("Файлов с изменениями: " + result.Summary.FilesChanged);
            AppendLog("BSL-комментариев заменено: " + result.Summary.BslCommentsRemoved);
            AppendLog("XML-комментариев удалено: " + result.Summary.XmlCommentsRemoved);
            AppendLog("Полей Comment очищено: " + result.Summary.CommentFieldsCleared);
            AppendLog("Пропущено: " + result.Summary.Skipped);
            AppendLog("Ошибок: " + result.Summary.Errors);
            AppendLog("Отчет: " + result.ReportPath);
            if (!string.IsNullOrEmpty(result.BackupPath))
            {
                AppendLog("Backup: " + result.BackupPath);
            }

            MessageBox.Show(
                this,
                result.Summary.Errors > 0 ? "Готово, но есть ошибки. Откройте отчет." : "Готово без ошибок.",
                "Готово",
                MessageBoxButtons.OK,
                result.Summary.Errors > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        private void CancelRun()
        {
            if (cancellation != null)
            {
                cancellation.Cancel();
                AppendLog("Запрошена остановка...");
            }
        }

        private void OpenLastReport()
        {
            if (!string.IsNullOrEmpty(lastReportPath) && File.Exists(lastReportPath))
            {
                Process.Start(new ProcessStartInfo(lastReportPath) { UseShellExecute = true });
            }
        }

        private void RunSelfTestFromUi()
        {
            try
            {
                Cleaner.RunSelfTests();
                MessageBox.Show(this, "Самотест пройден.", "Самотест", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Самотест не пройден", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void AppendLog(string text)
        {
            logTextBox.AppendText(text + Environment.NewLine);
        }
    }

    internal sealed class RunOptions
    {
        public string Root;
        public bool DryRun;
        public bool ValidateDryRun;
        public bool CreateBackup;
        public bool ExtendedScan;
        public int Jobs;
    }

    internal sealed class ProgressInfo
    {
        public long ProcessedFiles;
        public long TotalFiles;
        public long ChangedFiles;
        public long Errors;
        public string Message;
    }

    internal sealed class RunResult
    {
        public SummaryStats Summary;
        public string ReportPath;
        public string BackupPath;
        public bool Cancelled;
    }

    internal sealed class SummaryStats
    {
        public string Root;
        public bool DryRun;
        public long FilesSeen;
        public long FilesProcessed;
        public long FilesChanged;
        public long BslCommentsRemoved;
        public long XmlCommentsRemoved;
        public long CommentFieldsCleared;
        public long Errors;
        public long Skipped;
    }

    internal sealed class FileStats
    {
        public string Path;
        public bool Changed;
        public int BslCommentsRemoved;
        public int XmlCommentsRemoved;
        public int CommentFieldsCleared;
        public bool Skipped;
        public string SkipReason;
        public string Error;
    }

    internal sealed class DecodedText
    {
        public string Text;
        public Encoding Encoding;
        public byte[] Preamble;
    }

    internal sealed class SkipFileException : Exception
    {
        public SkipFileException(string message) : base(message)
        {
        }
    }

    internal static class Cleaner
    {
        private static readonly HashSet<string> DefaultExcludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".agents", ".codex", "__pycache__",
            "_1c_temp_ib", "_ibcmd_data", "_chrome_pdf_profile", "_report_render",
            "Clean1CCommentsReports"
        };

        private static readonly HashSet<string> BslExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".bsl", ".os"
        };

        private static readonly HashSet<string> XmlExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".xml", ".xsd", ".xsl", ".xslt"
        };

        private static readonly HashSet<string> XmlTextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".xml", ".xsd", ".xsl", ".xslt", ".html", ".htm", ".xhtml", ".txt"
        };

        private static readonly HashSet<string> DefaultTextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".bsl", ".os", ".xml", ".xsd", ".xsl", ".xslt", ".html", ".htm", ".xhtml", ".txt"
        };

        private static readonly HashSet<string> HardBinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".zip", ".7z", ".rar",
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
            ".epf", ".erf", ".cf", ".cfu", ".exe", ".dll", ".pdb"
        };

        private static readonly Regex XmlCommentRegex = new Regex("<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex CommentFieldRegex = new Regex(
            "<(?<tag>(?:[A-Za-z_][\\w.-]*:)?(?:Comment|Комментарий))(?<attrs>(?:\\s+[^<>]*?)?)>(?<body>.*?)</\\k<tag>>",
            RegexOptions.Singleline | RegexOptions.Compiled);

        public static RunResult Run(RunOptions options, CancellationToken token, Action<ProgressInfo> progress)
        {
            var root = Path.GetFullPath(options.Root);
            var summary = new SummaryStats();
            summary.Root = root;
            summary.DryRun = options.DryRun;

            if (progress != null)
            {
                progress(new ProgressInfo { Message = "Сканирую файлы..." });
            }

            var backupPath = options.CreateBackup ? MakeBackupPath(root) : null;
            var excludePaths = new List<string>();
            if (!string.IsNullOrEmpty(backupPath) && IsSubPathOf(backupPath, root))
            {
                excludePaths.Add(backupPath);
            }

            var files = EnumerateCandidateFiles(root, options.ExtendedScan, excludePaths, token);
            summary.FilesSeen = files.Count;

            if (progress != null)
            {
                progress(new ProgressInfo { TotalFiles = summary.FilesSeen, Message = "Файлов к проверке: " + summary.FilesSeen });
            }

            var details = new ConcurrentBag<FileStats>();
            long processed = 0;
            long changed = 0;
            long errors = 0;
            long skipped = 0;
            long filesProcessed = 0;
            long bslRemoved = 0;
            long xmlRemoved = 0;
            long fieldsCleared = 0;

            var parallelOptions = new ParallelOptions();
            parallelOptions.MaxDegreeOfParallelism = Math.Max(1, options.Jobs);
            parallelOptions.CancellationToken = token;

            try
            {
                Parallel.ForEach(files, parallelOptions, delegate(string file, ParallelLoopState state)
                {
                    if (token.IsCancellationRequested)
                    {
                        state.Stop();
                        return;
                    }

                    var fileStats = ProcessFile(file, root, options, backupPath);

                    if (fileStats.Skipped)
                    {
                        Interlocked.Increment(ref skipped);
                    }
                    else
                    {
                        Interlocked.Increment(ref filesProcessed);
                    }

                    if (!string.IsNullOrEmpty(fileStats.Error))
                    {
                        Interlocked.Increment(ref errors);
                    }

                    if (fileStats.Changed && string.IsNullOrEmpty(fileStats.Error))
                    {
                        Interlocked.Increment(ref changed);
                    }

                    Interlocked.Add(ref bslRemoved, fileStats.BslCommentsRemoved);
                    Interlocked.Add(ref xmlRemoved, fileStats.XmlCommentsRemoved);
                    Interlocked.Add(ref fieldsCleared, fileStats.CommentFieldsCleared);

                    if (fileStats.Changed || fileStats.Skipped || !string.IsNullOrEmpty(fileStats.Error))
                    {
                        details.Add(fileStats);
                    }

                    var current = Interlocked.Increment(ref processed);
                    if (progress != null && (current == summary.FilesSeen || current % 1000 == 0))
                    {
                        progress(new ProgressInfo
                        {
                            ProcessedFiles = current,
                            TotalFiles = summary.FilesSeen,
                            ChangedFiles = Interlocked.Read(ref changed),
                            Errors = Interlocked.Read(ref errors)
                        });
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }

            summary.FilesProcessed = filesProcessed;
            summary.FilesChanged = changed;
            summary.Errors = errors;
            summary.Skipped = skipped;
            summary.BslCommentsRemoved = bslRemoved;
            summary.XmlCommentsRemoved = xmlRemoved;
            summary.CommentFieldsCleared = fieldsCleared;

            var reportPath = WriteReport(summary, details, backupPath);
            return new RunResult
            {
                Summary = summary,
                ReportPath = reportPath,
                BackupPath = backupPath,
                Cancelled = token.IsCancellationRequested
            };
        }

        private static List<string> EnumerateCandidateFiles(string root, bool extendedScan, List<string> excludePaths, CancellationToken token)
        {
            var result = new List<string>();
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var current = stack.Pop();

                string[] dirs;
                try
                {
                    dirs = Directory.GetDirectories(current);
                }
                catch
                {
                    dirs = new string[0];
                }

                for (var i = 0; i < dirs.Length; i++)
                {
                    var name = Path.GetFileName(dirs[i]);
                    if (DefaultExcludedDirs.Contains(name))
                    {
                        continue;
                    }

                    var excluded = false;
                    for (var j = 0; j < excludePaths.Count; j++)
                    {
                        if (SamePath(dirs[i], excludePaths[j]) || IsSubPathOf(dirs[i], excludePaths[j]))
                        {
                            excluded = true;
                            break;
                        }
                    }

                    if (!excluded)
                    {
                        stack.Push(dirs[i]);
                    }
                }

                string[] files;
                try
                {
                    files = Directory.GetFiles(current);
                }
                catch
                {
                    files = new string[0];
                }

                for (var i = 0; i < files.Length; i++)
                {
                    var extension = Path.GetExtension(files[i]);
                    if (DefaultTextExtensions.Contains(extension))
                    {
                        result.Add(files[i]);
                    }
                    else if (extendedScan && !HardBinaryExtensions.Contains(extension))
                    {
                        result.Add(files[i]);
                    }
                }
            }

            return result;
        }

        private static FileStats ProcessFile(string file, string root, RunOptions options, string backupPath)
        {
            var stats = new FileStats();
            stats.Path = MakeRelativePath(root, file);
            var extension = Path.GetExtension(file);

            if (options.ExtendedScan && HardBinaryExtensions.Contains(extension))
            {
                stats.Skipped = true;
                stats.SkipReason = "Пропущен известный бинарный формат.";
                return stats;
            }

            DecodedText decoded;
            try
            {
                decoded = DecodeText(File.ReadAllBytes(file));
            }
            catch (SkipFileException ex)
            {
                stats.Skipped = true;
                stats.SkipReason = ex.Message;
                return stats;
            }
            catch (Exception ex)
            {
                stats.Error = "Не удалось прочитать как текст: " + ex.Message;
                return stats;
            }

            var changedText = decoded.Text;
            var isBsl = BslExtensions.Contains(extension);
            var isXmlText = XmlTextExtensions.Contains(extension);
            var xmlByContent = LooksLikeXml(changedText);

            if (options.ExtendedScan && !isBsl && !isXmlText && !xmlByContent)
            {
                stats.Skipped = true;
                stats.SkipReason = "Нет признаков XML/BSL-файла выгрузки 1С.";
                return stats;
            }

            if (isBsl)
            {
                changedText = StripBslComments(changedText, out stats.BslCommentsRemoved);
            }

            if (isXmlText || xmlByContent)
            {
                changedText = CleanXmlLikeText(changedText, out stats.XmlCommentsRemoved, out stats.CommentFieldsCleared);
            }

            stats.Changed = !string.Equals(changedText, decoded.Text, StringComparison.Ordinal);
            if (!stats.Changed)
            {
                return stats;
            }

            if ((!options.DryRun || options.ValidateDryRun) && ShouldParseAsXml(extension, changedText))
            {
                try
                {
                    ValidateXml(changedText);
                }
                catch (Exception ex)
                {
                    stats.Error = "XML стал невалидным после очистки: " + ex.Message;
                    return stats;
                }
            }

            if (options.DryRun)
            {
                return stats;
            }

            try
            {
                if (!string.IsNullOrEmpty(backupPath))
                {
                    CopyBackup(file, root, backupPath);
                }

                WriteFile(file, EncodeText(changedText, decoded.Encoding, decoded.Preamble));
            }
            catch (Exception ex)
            {
                stats.Error = "Не удалось записать файл: " + ex.Message;
            }

            return stats;
        }

        private static DecodedText DecodeText(byte[] data)
        {
            if (LooksBinary(data))
            {
                throw new SkipFileException("Пропущен бинарный файл.");
            }

            var decoded = new DecodedText();
            if (HasPrefix(data, new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                decoded.Encoding = new UTF8Encoding(false, true);
                decoded.Preamble = new byte[] { 0xEF, 0xBB, 0xBF };
                decoded.Text = decoded.Encoding.GetString(data, 3, data.Length - 3);
                return decoded;
            }

            if (HasPrefix(data, new byte[] { 0xFF, 0xFE }))
            {
                decoded.Encoding = Encoding.Unicode;
                decoded.Preamble = new byte[] { 0xFF, 0xFE };
                decoded.Text = decoded.Encoding.GetString(data, 2, data.Length - 2);
                return decoded;
            }

            if (HasPrefix(data, new byte[] { 0xFE, 0xFF }))
            {
                decoded.Encoding = Encoding.BigEndianUnicode;
                decoded.Preamble = new byte[] { 0xFE, 0xFF };
                decoded.Text = decoded.Encoding.GetString(data, 2, data.Length - 2);
                return decoded;
            }

            try
            {
                decoded.Encoding = new UTF8Encoding(false, true);
                decoded.Preamble = new byte[0];
                decoded.Text = decoded.Encoding.GetString(data);
                return decoded;
            }
            catch (DecoderFallbackException)
            {
                decoded.Encoding = Encoding.GetEncoding(1251);
                decoded.Preamble = new byte[0];
                decoded.Text = decoded.Encoding.GetString(data);
                return decoded;
            }
        }

        private static byte[] EncodeText(string text, Encoding encoding, byte[] preamble)
        {
            var body = encoding.GetBytes(text);
            if (preamble == null || preamble.Length == 0)
            {
                return body;
            }

            var result = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, result, preamble.Length, body.Length);
            return result;
        }

        private static bool LooksBinary(byte[] data)
        {
            if (data.Length == 0)
            {
                return false;
            }

            if (HasPrefix(data, new byte[] { 0x50, 0x4B, 0x03, 0x04 }) ||
                HasPrefix(data, new byte[] { 0x89, 0x50, 0x4E, 0x47 }) ||
                HasPrefix(data, new byte[] { 0xFF, 0xD8, 0xFF }) ||
                HasPrefix(data, new byte[] { 0x47, 0x49, 0x46, 0x38 }) ||
                HasPrefix(data, new byte[] { 0x42, 0x4D }) ||
                HasPrefix(data, new byte[] { 0x25, 0x50, 0x44, 0x46 }) ||
                HasPrefix(data, new byte[] { 0x4D, 0x5A }))
            {
                return true;
            }

            var prefix = Math.Min(4096, data.Length);
            for (var i = 0; i < prefix; i++)
            {
                if (data[i] == 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasPrefix(byte[] data, byte[] prefix)
        {
            if (data.Length < prefix.Length)
            {
                return false;
            }

            for (var i = 0; i < prefix.Length; i++)
            {
                if (data[i] != prefix[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static string StripBslComments(string text, out int removed)
        {
            removed = 0;
            if (text.IndexOf("//", StringComparison.Ordinal) < 0)
            {
                return text;
            }

            var result = new StringBuilder(text.Length);
            var inString = false;
            var i = 0;

            while (i < text.Length)
            {
                var ch = text[i];

                if (inString)
                {
                    result.Append(ch);
                    if (ch == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            result.Append(text[i + 1]);
                            i += 2;
                            continue;
                        }
                        inString = false;
                    }
                    i++;
                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    result.Append(ch);
                    i++;
                    continue;
                }

                if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    removed++;
                    result.Append("// ЗАГЛУШКА");
                    i += 2;
                    while (i < text.Length && text[i] != '\r' && text[i] != '\n')
                    {
                        i++;
                    }
                    continue;
                }

                result.Append(ch);
                i++;
            }

            return result.ToString();
        }

        private static string CleanXmlLikeText(string text, out int xmlCommentsRemoved, out int commentFieldsCleared)
        {
            var xmlCounter = 0;
            if (text.IndexOf("<!--", StringComparison.Ordinal) >= 0)
            {
                text = XmlCommentRegex.Replace(text, delegate(Match match)
                {
                    xmlCounter++;
                    return "";
                });
            }
            xmlCommentsRemoved = xmlCounter;

            var fieldCounter = 0;
            if (text.IndexOf("Comment", StringComparison.Ordinal) >= 0 || text.IndexOf("Комментарий", StringComparison.Ordinal) >= 0)
            {
                text = CommentFieldRegex.Replace(text, delegate(Match match)
                {
                    var replacement = "<" + match.Groups["tag"].Value + match.Groups["attrs"].Value + "/>";
                    if (!string.Equals(replacement, match.Value, StringComparison.Ordinal))
                    {
                        fieldCounter++;
                    }
                    return replacement;
                });
            }

            commentFieldsCleared = fieldCounter;
            return text;
        }

        private static bool LooksLikeXml(string text)
        {
            var start = text.TrimStart('\uFEFF', '\r', '\n', '\t', ' ');
            return start.StartsWith("<?xml", StringComparison.Ordinal) || start.StartsWith("<MetaDataObject", StringComparison.Ordinal);
        }

        private static bool ShouldParseAsXml(string extension, string text)
        {
            if (!XmlExtensions.Contains(extension) && !LooksLikeXml(text))
            {
                return false;
            }

            var start = text.TrimStart('\uFEFF', '\r', '\n', '\t', ' ');
            return start.StartsWith("<", StringComparison.Ordinal);
        }

        private static void ValidateXml(string text)
        {
            var settings = new XmlReaderSettings();
            settings.DtdProcessing = DtdProcessing.Ignore;
            settings.XmlResolver = null;
            using (var reader = XmlReader.Create(new StringReader(text.TrimStart('\uFEFF')), settings))
            {
                while (reader.Read())
                {
                }
            }
        }

        private static void CopyBackup(string file, string root, string backupPath)
        {
            var target = Path.Combine(backupPath, MakeRelativePath(root, file));
            var directory = Path.GetDirectoryName(target);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.Copy(file, target, true);
        }

        private static void WriteFile(string file, byte[] data)
        {
            var directory = Path.GetDirectoryName(file);
            var temp = Path.Combine(directory, "." + Path.GetFileName(file) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(temp, data);
            File.Copy(temp, file, true);
            File.Delete(temp);
        }

        private static string MakeBackupPath(string root)
        {
            var parent = Directory.GetParent(root);
            var baseName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent == null || string.IsNullOrEmpty(baseName))
            {
                parent = new DirectoryInfo(Path.GetTempPath());
                baseName = "configuration";
            }
            return Path.Combine(parent.FullName, baseName + "_backup_comments_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        }

        private static string WriteReport(SummaryStats summary, ConcurrentBag<FileStats> details, string backupPath)
        {
            var reportDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Reports");
            Directory.CreateDirectory(reportDir);

            var rootName = Path.GetFileName(summary.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(rootName))
            {
                rootName = "configuration";
            }

            var reportPath = Path.Combine(reportDir, rootName + "_clean_report_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
            using (var writer = new StreamWriter(reportPath, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("{");
                writer.WriteLine("  \"summary\": {");
                WriteJsonProperty(writer, "root", summary.Root, true, 4);
                WriteJsonProperty(writer, "dry_run", summary.DryRun, true, 4);
                WriteJsonProperty(writer, "files_seen", summary.FilesSeen, true, 4);
                WriteJsonProperty(writer, "files_processed", summary.FilesProcessed, true, 4);
                WriteJsonProperty(writer, "files_changed", summary.FilesChanged, true, 4);
                WriteJsonProperty(writer, "bsl_comments_removed", summary.BslCommentsRemoved, true, 4);
                WriteJsonProperty(writer, "xml_comments_removed", summary.XmlCommentsRemoved, true, 4);
                WriteJsonProperty(writer, "comment_fields_cleared", summary.CommentFieldsCleared, true, 4);
                WriteJsonProperty(writer, "skipped", summary.Skipped, true, 4);
                WriteJsonProperty(writer, "errors", summary.Errors, !string.IsNullOrEmpty(backupPath), 4);
                if (!string.IsNullOrEmpty(backupPath))
                {
                    WriteJsonProperty(writer, "backup", backupPath, false, 4);
                }
                writer.WriteLine("  },");
                writer.WriteLine("  \"files\": [");

                var first = true;
                foreach (var item in details)
                {
                    if (!first)
                    {
                        writer.WriteLine(",");
                    }
                    first = false;
                    writer.WriteLine("    {");
                    WriteJsonProperty(writer, "path", item.Path, true, 6);
                    WriteJsonProperty(writer, "changed", item.Changed, true, 6);
                    WriteJsonProperty(writer, "bsl_comments_removed", item.BslCommentsRemoved, true, 6);
                    WriteJsonProperty(writer, "xml_comments_removed", item.XmlCommentsRemoved, true, 6);
                    WriteJsonProperty(writer, "comment_fields_cleared", item.CommentFieldsCleared, true, 6);
                    WriteJsonProperty(writer, "skipped", item.Skipped, true, 6);
                    WriteJsonProperty(writer, "skip_reason", item.SkipReason, true, 6);
                    WriteJsonProperty(writer, "error", item.Error, false, 6);
                    writer.Write("    }");
                }
                writer.WriteLine();
                writer.WriteLine("  ]");
                writer.WriteLine("}");
            }

            return reportPath;
        }

        private static void WriteJsonProperty(StreamWriter writer, string name, string value, bool comma, int indent)
        {
            writer.Write(new string(' ', indent));
            writer.Write("\"");
            writer.Write(JsonEscape(name));
            writer.Write("\": ");
            if (value == null)
            {
                writer.Write("null");
            }
            else
            {
                writer.Write("\"");
                writer.Write(JsonEscape(value));
                writer.Write("\"");
            }
            if (comma)
            {
                writer.Write(",");
            }
            writer.WriteLine();
        }

        private static void WriteJsonProperty(StreamWriter writer, string name, bool value, bool comma, int indent)
        {
            writer.Write(new string(' ', indent));
            writer.Write("\"");
            writer.Write(JsonEscape(name));
            writer.Write("\": ");
            writer.Write(value ? "true" : "false");
            if (comma)
            {
                writer.Write(",");
            }
            writer.WriteLine();
        }

        private static void WriteJsonProperty(StreamWriter writer, string name, long value, bool comma, int indent)
        {
            writer.Write(new string(' ', indent));
            writer.Write("\"");
            writer.Write(JsonEscape(name));
            writer.Write("\": ");
            writer.Write(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (comma)
            {
                writer.Write(",");
            }
            writer.WriteLine();
        }

        private static string JsonEscape(string value)
        {
            if (value == null)
            {
                return null;
            }

            var sb = new StringBuilder(value.Length + 16);
            for (var i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                switch (ch)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (ch < 32)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)ch).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(ch);
                        }
                        break;
                }
            }
            return sb.ToString();
        }

        private static string MakeRelativePath(string root, string file)
        {
            var rootUri = new Uri(AppendDirectorySeparatorChar(Path.GetFullPath(root)));
            var fileUri = new Uri(Path.GetFullPath(file));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private static string AppendDirectorySeparatorChar(string path)
        {
            if (!path.EndsWith(Path.DirectorySeparatorChar.ToString()) && !path.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
            {
                return path + Path.DirectorySeparatorChar;
            }
            return path;
        }

        private static bool SamePath(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSubPathOf(string path, string possibleParent)
        {
            var fullPath = AppendDirectorySeparatorChar(Path.GetFullPath(path));
            var fullParent = AppendDirectorySeparatorChar(Path.GetFullPath(possibleParent));
            return fullPath.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase);
        }

        public static void RunSelfTests()
        {
            int removed;
            var bsl =
                "Адрес = \"http://example.org/a//b\"; // удалить\r\n" +
                "ТекстЗапроса =\r\n" +
                "\"ВЫБРАТЬ\r\n" +
                "|////////////////////////////////////////////////////////////////////////////////\r\n" +
                "|ГДЕ Поле = \"\"A//B\"\"\"; // хвост\r\n" +
                "\t// целая строка\r\n" +
                "Возврат Адрес;\r\n";
            var cleanedBsl = StripBslComments(bsl, out removed);
            Assert(removed == 3, "BSL: неверное число удаленных комментариев.");
            Assert(cleanedBsl.IndexOf("удалить", StringComparison.Ordinal) < 0, "BSL: хвостовой комментарий не удален.");
            Assert(cleanedBsl.IndexOf("хвост", StringComparison.Ordinal) < 0, "BSL: второй хвостовой комментарий не удален.");
            Assert(cleanedBsl.IndexOf("целая строка", StringComparison.Ordinal) < 0, "BSL: строка-комментарий не удалена.");
            Assert(Regex.Matches(cleanedBsl, Regex.Escape("// ЗАГЛУШКА")).Count == 3, "BSL: комментарии не заменены заглушкой.");
            Assert(cleanedBsl.Split('\n').Length == bsl.Split('\n').Length, "BSL: число строк изменилось.");
            Assert(cleanedBsl.IndexOf("http://example.org/a//b", StringComparison.Ordinal) >= 0, "BSL: URL внутри строки поврежден.");
            Assert(cleanedBsl.IndexOf("|////////////////////////////////////////////////////////////////////////////////", StringComparison.Ordinal) >= 0, "BSL: строка запроса повреждена.");
            Assert(cleanedBsl.IndexOf("\"\"A//B\"\"", StringComparison.Ordinal) >= 0, "BSL: экранированная строка повреждена.");

            int xmlComments;
            int fields;
            var xml =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<MetaDataObject xmlns:xr=\"urn:test\">\n" +
                "  <!-- удалить -->\n" +
                "  <Comment>Текст</Comment>\n" +
                "  <xr:Comment>Текст XR</xr:Comment>\n" +
                "  <Name>Объект</Name>\n" +
                "</MetaDataObject>\n";
            var cleanedXml = CleanXmlLikeText(xml, out xmlComments, out fields);
            Assert(xmlComments == 1, "XML: комментарий не удален.");
            Assert(fields == 2, "XML: поля Comment очищены не полностью.");
            Assert(cleanedXml.IndexOf("<!--", StringComparison.Ordinal) < 0, "XML: остался XML-комментарий.");
            Assert(cleanedXml.IndexOf("<Comment/>", StringComparison.Ordinal) >= 0, "XML: поле Comment очищено неверно.");
            Assert(cleanedXml.IndexOf("<xr:Comment/>", StringComparison.Ordinal) >= 0, "XML: поле xr:Comment очищено неверно.");
            ValidateXml(cleanedXml);

            var withBom = new byte[] { 0xEF, 0xBB, 0xBF, 0x3C, 0x3F, 0x78, 0x6D, 0x6C, 0x20, 0x76, 0x65, 0x72, 0x73, 0x69, 0x6F, 0x6E, 0x3D, 0x22, 0x31, 0x2E, 0x30, 0x22, 0x3F, 0x3E, 0x3C, 0x72, 0x2F, 0x3E };
            var decoded = DecodeText(withBom);
            Assert(!decoded.Text.StartsWith("\uFEFF", StringComparison.Ordinal), "UTF-8 BOM не должен попадать в текст для XML-валидации.");
            ValidateXml(decoded.Text);
            var encoded = EncodeText(decoded.Text, decoded.Encoding, decoded.Preamble);
            Assert(HasPrefix(encoded, new byte[] { 0xEF, 0xBB, 0xBF }), "UTF-8 BOM должен сохраняться при записи.");

            try
            {
                DecodeText(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00 });
                throw new InvalidOperationException("ZIP-сигнатура не распознана как бинарная.");
            }
            catch (SkipFileException)
            {
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
