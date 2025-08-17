using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EditorUtility
{
    public class DialogueCheckerWindow : EditorWindow
    {
        /* エンジンとモデルの種類(Whisperのみ) */
        public enum RecognitionEngine { Google, Whisper, Sphinx }
        public enum WhisperModel { Tiny, Base, Small, Medium }

        /* 設定項目 */
        private string pythonExecutablePath = "";
        private string csvFilePath = "";
        private string audioFolderPath = "";
        private string columnName = "context";                               // デフォルトの列名
        private string rowId = "";                                           // 比較したい行のID
        private RecognitionEngine selectedEngine = RecognitionEngine.Google; // 使用する音声認識エンジン
        private string languageCode = "ja-JP";                               // デフォルトの言語コード (Google=ja-JP, Whisper=ja, Sphinx=ja)
        private string modelName = "base";                                   // Whisperモデル名 (デフォルトは"base")
        private WhisperModel selectedWhisperModel = WhisperModel.Base;       // Whisperモデルの選択 (Tiny, Base, Small, Medium)

        /* プロセス管理 */
        private Process pythonProcess;
        private StringBuilder outputBuilder;
        private StringBuilder errorBuilder;

        /* CSVプレビュー用の変数 */
        private Vector2 csvScrollPosition;
        private string[] csvHeaders = new string[0];
        private List<string[]> csvData = new List<string[]>();
        private string loadedCsvFilePath = "";
        
        /* 音声ファイルプレビュー用の変数 */
        private Vector2 audioMapScrollPosition;
        private List<string> audioFilePathsInFolder = new List<string>();
        private List<string> availableCsvIds = new List<string>();
        private Dictionary<string, int> audioIdSelection = new Dictionary<string, int>();
        private string loadedAudioFolderPath = "";
        private string tempJsonPath = ""; // 一時的なJSONファイルのパス
        
        /* Pythonの実行ファイルのパス */
        private const string PYTHON_PATH_KEY = "DialogueChecker_PythonPath";
        
        // Pythonに渡す単一タスクの構造体
        [System.Serializable]
        private class ProcessingTask
        {
            public string id;
            public string audioPath;
            public string scriptText;
        }
        
        // TaskリストをJSON化するためのラッパークラス
        [System.Serializable]
        private class TaskListWrapper
        {
            public List<ProcessingTask> tasks; 
        }
        
        // Pythonから結果を受け取るためのラッパークラス
        [System.Serializable]
        private class BatchResult
        {
            public List<PythonResult> results;
        }

        // 結果を保持するためのインナークラス
        [System.Serializable]
        private class PythonResult
        {
            public string id;
            public float similarity;
            public string script_text;
            public string recognized_text;
            public string[] diff;
            public string error;
        }

        [MenuItem("Tools/Dialogue Checker")]
        public static void ShowWindow()
        {
            GetWindow<DialogueCheckerWindow>("Dialogue Checker");
        }

        private void OnEnable()
        {
            pythonExecutablePath = EditorPrefs.GetString(PYTHON_PATH_KEY, "python");
        }

        private void OnDestroy()
        {
            if (pythonProcess != null && !pythonProcess.HasExited)
            {
                pythonProcess.Kill();
            }
        }

        private void OnGUI()
        {
            GUILayout.Label("Dialogue Checker", EditorStyles.boldLabel);

            // Python実行パスのUI
            EditorGUILayout.BeginHorizontal();
            string newPath = EditorGUILayout.TextField("Python Path", pythonExecutablePath);
            if (newPath != pythonExecutablePath)
            {
                pythonExecutablePath = newPath;
                EditorPrefs.SetString(PYTHON_PATH_KEY, pythonExecutablePath);
            }

            // パス選択ボタン
            if (GUILayout.Button("Browse", GUILayout.Width(80)))
            {
                string path = UnityEditor.EditorUtility.OpenFilePanel("Select Python Executable", "", "exe");
                if (!string.IsNullOrEmpty(path))
                {
                    pythonExecutablePath = path;
                    EditorPrefs.SetString(PYTHON_PATH_KEY, pythonExecutablePath);
                }
            }

            EditorGUILayout.EndHorizontal();

            // 音声認識エンジンと言語を選択
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                selectedEngine = (RecognitionEngine)EditorGUILayout.EnumPopup("Recognition Engine", selectedEngine);
                if (check.changed)
                {
                    switch (selectedEngine)
                    {
                        case RecognitionEngine.Google:
                            languageCode = "ja-JP";
                            break;
                        case RecognitionEngine.Whisper:
                            languageCode = "ja";
                            break;
                        case RecognitionEngine.Sphinx:
                            languageCode = "en-US";
                            break;
                    }
                }
            }
            
            languageCode = EditorGUILayout.TextField("Language Code", languageCode);
            
            // 選択中のモデルによってヒントを表示
            string hintText = "";
            switch (selectedEngine)
            {
                case RecognitionEngine.Google:
                    hintText = "Google: 「ja-JP」や「en-US」のようなIETF言語タグを使用してください";
                    break;

                case RecognitionEngine.Whisper:
                    hintText = "Whisper: 「ja」や「en」、「ko」のようなISO 639-1言語コードを使用してください";
                    break;
                
                case RecognitionEngine.Sphinx:
                    hintText = "Sphinx: 「ja-JP」などを使用しますが、別途言語モデルの導入が必要です(初期設定：「en-US」)";
                    break;
            }
            EditorGUILayout.HelpBox(hintText, MessageType.Info);

            // Whisperモデル名の選択
            if (selectedEngine == RecognitionEngine.Whisper)
            {
                selectedWhisperModel = (WhisperModel)EditorGUILayout.EnumPopup("Model Name", selectedWhisperModel);
            }

            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("Inputs", EditorStyles.boldLabel);

            // CSVファイル選択
            EditorGUILayout.BeginHorizontal();
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                csvFilePath = EditorGUILayout.TextField("CSV File Path", csvFilePath);
                if (GUILayout.Button("Browse", GUILayout.Width(80)))
                {
                    string path = UnityEditor.EditorUtility.OpenFilePanel("Select CSV File", "", "csv");
                    if (!string.IsNullOrEmpty(path))
                    {
                        csvFilePath = path;
                        GUI.FocusControl(null); // フォーカスを外してTextFieldを更新
                    }
                }

                if (check.changed && !string.IsNullOrEmpty(csvFilePath))
                {
                    LoadCsvFile(csvFilePath);
                }
            }
            
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox("CSVファイルは、1行目にヘッダーがあり、2行目以降にデータがある形式である必要があります。\n" +
                                    "ID列は1列目にあり、比較したい行のIDを入力してください。", MessageType.Info);

            DisplayCsvContent();

            // 音声ファイルを選択
            EditorGUILayout.BeginHorizontal();
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                audioFolderPath = EditorGUILayout.TextField("Audio Folder Path", audioFolderPath);
                if (GUILayout.Button("Browse", GUILayout.Width(80)))
                {
                    string path = UnityEditor.EditorUtility.OpenFolderPanel("Select Audio Folder", "", "");
                    if (!string.IsNullOrEmpty(path))
                    {
                        audioFolderPath = path;
                        GUI.FocusControl(null); // フォーカスを外してTextFieldを更新
                    }
                }

                // ChangeCheckScopeを使って、パスの変更を確実に検知する
                if (check.changed && !string.IsNullOrEmpty(audioFolderPath))
                {
                    LoadAudioFolder(audioFolderPath);
                }
            }
            
            EditorGUILayout.EndHorizontal();
            DisplayAudioFileMappingUI();

            // パラメーター入力
            columnName = EditorGUILayout.TextField("Column Name to Compare", columnName);
            EditorGUILayout.Space(10);

            GUI.enabled = (pythonProcess == null || pythonProcess.HasExited);

            // 比較実行ボタン
            if (GUILayout.Button("Compare All Files in Folder"))
            {
                if (string.IsNullOrEmpty(csvFilePath) || string.IsNullOrEmpty(audioFolderPath) || string.IsNullOrEmpty(columnName))
                {
                    UnityEngine.Debug.LogError("CSV File, Audio Folder, and Column Name must be specified.");
                    return;
                }
                
                RunBatchProcessAsync();
            }
            
            GUI.enabled = true;
        }

        /// <summary>
        /// pythonスクリプトを実行して音声認識と比較を行う
        /// </summary>
        private void RunBatchProcessAsync()
        {
            // 処理するタスクのリストを作成
            List<ProcessingTask> tasks = new List<ProcessingTask>();

            int scriptColumnIndex = Array.IndexOf(csvHeaders, columnName);
            if (scriptColumnIndex == -1)
            {
                UnityEngine.Debug.LogError($"Column '{columnName}' not found in CSV headers.");
                return;
            }

            foreach (var mapping in audioIdSelection)
            {
                string audioPath = mapping.Key;
                int selectedIndex = mapping.Value;

                if (selectedIndex > 0)
                {
                    string selectedId = availableCsvIds[selectedIndex];
                    
                    // CSVデータから対応する行を探す
                    string[] rowData = csvData.FirstOrDefault(row => row[0] == selectedId);
                    if (rowData != null)
                    {
                        tasks.Add(new ProcessingTask
                        {
                            id = selectedId,
                            audioPath = audioPath,
                            scriptText = rowData[scriptColumnIndex],
                        });
                    }
                }
            }

            if (tasks.Count == 0)
            {
                UnityEngine.Debug.LogWarning("No matching audio files found for the specified IDs in the CSV.");
                return;
            }
            
            // タスクリストをJSONに変換
            string tasksJson = JsonUtility.ToJson(new TaskListWrapper { tasks = tasks });
            
            // 一時ファイルにJSONを書き込む
            tempJsonPath = Path.GetTempFileName();
            File.WriteAllText(tempJsonPath, tasksJson);
            
            // Pythonスクリプトを実行
            outputBuilder = new StringBuilder();
            errorBuilder = new StringBuilder();
            
            string pythonScriptPath = Path.Combine(Application.dataPath, "Python", "RecognizeAndCompare.py");
            string engineName = selectedEngine.ToString().ToLower();
            string whisperModelName = (selectedEngine == RecognitionEngine.Whisper) ? selectedWhisperModel.ToString().ToLower() : "none";
            
            // 引数を組み立てる
            string arguments = $"\"{pythonScriptPath}\" \"{engineName}\" \"{languageCode}\" \"{whisperModelName}\" \"{tempJsonPath}\"";
            
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = pythonExecutablePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            
            // Pythonの出力エンコーディングをUTF-8に設定(文字化け防止)
            startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            pythonProcess = new Process { StartInfo = startInfo };
            
            // Pythonプロセスの出力を非同期で受け取るためのイベントハンドラを設定
            pythonProcess.Exited += OnProcessExited;
            pythonProcess.EnableRaisingEvents = true;
            
            // 出力とエラーの受信を設定
            pythonProcess.OutputDataReceived += OnOutputDataReceived;
            pythonProcess.ErrorDataReceived += OnErrorDataReceived;
            
            pythonProcess.Start();
            
            // 非同期読み取りを開始する
            pythonProcess.BeginOutputReadLine();
            pythonProcess.BeginErrorReadLine();

            UnityEditor.EditorUtility.DisplayProgressBar("Running python script", "Running python script", 0f);
            UnityEngine.Debug.Log("Running Python script...");
        }

        /// <summary>
        /// 標準出力から1行データを受信したときのイベントハンドラ
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data))
                return;

            Match match = Regex.Match(e.Data, @"\s*(\d+)%");
            if (match.Success)
            {
                float progress = float.Parse(match.Groups[1].Value) / 100.0f;
                EditorApplication.delayCall += () =>
                {
                    UnityEditor.EditorUtility.DisplayProgressBar("Downloading Model",
                        $"{match.Groups[1].Value}% completed", progress);
                };
            }

            outputBuilder.AppendLine(e.Data);
        }

        /// <summary>
        /// 標準エラーから1行データを受信したときのイベントハンドラ
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data))
                return;

            EditorApplication.delayCall += () =>
            {
                UnityEditor.EditorUtility.DisplayProgressBar("Running Python Script", "Processing...", 0.5f);
            };

            errorBuilder.AppendLine(e.Data);
        }

        /// <summary>
        /// プロセスが終了したときのイベントハンドラ
        /// </summary>
        private void OnProcessExited(object sender, EventArgs e)
        {
            EditorApplication.delayCall += () =>
            {
                UnityEditor.EditorUtility.ClearProgressBar();

                string output = outputBuilder.ToString(); // stdoutからの最終結果JSON
                string error = errorBuilder.ToString();   // stderrからの進捗レポートやエラー
                
                if (!string.IsNullOrEmpty(error))
                {
                    // これは進捗レポートやPython内のエラーを含むので、情報としてログに出す
                    UnityEngine.Debug.Log($"[Python Process Info]:\n{error}");
                }
                
                if (string.IsNullOrWhiteSpace(output))
                {
                    UnityEngine.Debug.LogError("Python script did not produce a final result on stdout. Check the [Python Process Info] log above for errors.");
                    Repaint();
                    return;
                }
                
                try
                {
                    BatchResult batchResult = JsonUtility.FromJson<BatchResult>(output);
                    if (batchResult == null || batchResult.results == null)
                    {
                        // JSONパースに失敗した場合
                        UnityEngine.Debug.LogError($"Failed to parse JSON result from Python. Raw output:\n{output}");
                    }
                    else
                    {
                        UnityEngine.Debug.Log($"--- Batch Processing Complete: {batchResult.results.Count} files processed ---");
                        foreach (var result in batchResult.results)
                        {
                            DisplayResult(result); // 個別の結果を表示するメソッドを呼び出す
                        }
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"An error occurred while parsing JSON from Python: {ex.Message}\nRaw Output:\n{output}");
                }

                Repaint();
            };
        }

        /// <summary>
        /// pythonスクリプトの結果をUnityのコンソールに表示する
        /// </summary>
        /// <param name="result">出力された結果</param>
        private void DisplayResult(PythonResult result)
        {
            if (!string.IsNullOrEmpty(result.error))
            {
                UnityEngine.Debug.LogError($"Python script error for ID {result.id}: {result.error}");
                return;
            }

            UnityEngine.Debug.Log($"Comparison Result for ID: {result.id}");
            UnityEngine.Debug.Log($"<color=cyan>Similarity: {result.similarity:P2}</color>");
            UnityEngine.Debug.Log($"<b>Script:</b> {result.script_text}");
            UnityEngine.Debug.Log($"<b>Recognized:</b> {result.recognized_text}");

            if (result.diff != null && result.diff.Length > 0)
            {
                UnityEngine.Debug.LogWarning("Differences found:");
                foreach (var line in result.diff)
                {
                    if (line.StartsWith("+"))
                        UnityEngine.Debug.Log($"<color=green>{line}</color>");
                    else if (line.StartsWith("-"))
                        UnityEngine.Debug.Log($"<color=red>{line}</color>");
                    else if (line.StartsWith("@@"))
                        continue;
                    else
                        UnityEngine.Debug.Log(line);
                }
            }
            else if (string.IsNullOrEmpty(result.error))
            {
                UnityEngine.Debug.Log("<color=green>The texts are identical!</color>");
            }
        }

        /// <summary>
        /// CSVファイルを読み込み、ヘッダーとデータを取得する
        /// </summary>
        /// <param name="filePath">CSVのファイルパス</param>
        private void LoadCsvFile(string filePath)
        {
            try
            {
                // データを初期化
                csvHeaders = new string[0];
                csvData.Clear();
                availableCsvIds.Clear();

                if (!File.Exists(filePath))
                    return;

                string[] lines = File.ReadAllLines(filePath);
                if (lines.Length > 0)
                {
                    // 各ヘッダーから空白を除去
                    csvHeaders = lines[0].Split(',').Select(h => h.Trim()).ToArray();
            
                    for (int i = 1; i < lines.Length; i++)
                    {
                        // 空の行は無視する
                        if (string.IsNullOrWhiteSpace(lines[i])) continue;

                        // 各セルから空白を除去
                        csvData.Add(lines[i].Split(',').Select(cell => cell.Trim()).ToArray());
                    }
                }

                // クリーンナップされたデータからIDリストを再作成
                availableCsvIds = new List<string> { "Unassigned" };
                availableCsvIds.AddRange(csvData.Select(row => row[0]));

                loadedCsvFilePath = filePath;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"Failed to load CSV file: {e.Message}");
                loadedCsvFilePath = null;
            }
        }
        
        /// <summary>
        /// 読み込んだCSVファイルの内容をプレビュー表示する
        /// </summary>
        private void DisplayCsvContent()
        {
            if (csvData.Count == 0 || csvHeaders.Length == 0)
                return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("CSV Content Preview", EditorStyles.boldLabel);

            // 各列の最大幅を計算する
            float[] columnWidths = new float[csvHeaders.Length];
            GUIStyle labelStyle = EditorStyles.label;
            GUIStyle buttonStyle = GUI.skin.button;
            float padding = 10.0f; // セルの左右の余白

            // ヘッダーの幅を計算
            for (int i = 0; i < csvHeaders.Length; i++)
            {
                float width = EditorStyles.boldLabel.CalcSize(new GUIContent(csvHeaders[i])).x + padding;
                columnWidths[i] = width;
            }

            // データ行の幅を計算し、最大値を保持
            foreach (var rowData in csvData)
            {
                for (int i = 0; i < rowData.Length && i < columnWidths.Length; i++)
                {
                    GUIStyle style = (i == 0) ? buttonStyle : labelStyle;
                    float width = style.CalcSize(new GUIContent(rowData[i])).x + padding;
                    if (width > columnWidths[i])
                    {
                        columnWidths[i] = width;
                    }
                }
            }

            // スクロールビューを開始
            csvScrollPosition = EditorGUILayout.BeginScrollView(csvScrollPosition, GUILayout.Height(150));

            // ヘッダー行を計算した幅で描画
            EditorGUILayout.BeginHorizontal();
            for (int i = 0; i < csvHeaders.Length; i++)
            {
                GUILayout.Label(csvHeaders[i], EditorStyles.boldLabel, GUILayout.Width(columnWidths[i]));
            }
            EditorGUILayout.EndHorizontal();

            // データ行を計算した幅で描画
            foreach (var rowData in csvData)
            {
                EditorGUILayout.BeginHorizontal();
                for (int i = 0; i < rowData.Length && i < columnWidths.Length; i++)
                {
                    if (i == 0) // 1列目はボタン
                    {
                        if (GUILayout.Button(rowData[i], GUILayout.Width(columnWidths[i])))
                        {
                            rowId = rowData[i];
                            GUI.FocusControl(null);
                        }
                    }
                    else // 2列目以降はラベル
                    {
                        GUILayout.Label(rowData[i], GUILayout.Width(columnWidths[i]));
                    }
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.Space();
        }

        /// <summary>
        /// 音声ファイルのマッピングUIを表示する
        /// </summary>
        /// <param name="folderPath">音声フォルダのパス</param>
        private void LoadAudioFolder(string folderPath)
        {
            try
            {
                audioFilePathsInFolder = Directory.GetFiles(folderPath)
                    .Where(p => p.EndsWith(".wav") || p.EndsWith(".mp3") || p.EndsWith(".flac"))
                    .ToList();

                audioIdSelection.Clear();
                foreach (var path in audioFilePathsInFolder)
                {
                    audioIdSelection[path] = 0;
                }

                loadedAudioFolderPath = folderPath;
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"Failed to load audio floder: {e.Message}");
                loadedAudioFolderPath = null;
            }
        }

        private void DisplayAudioFileMappingUI()
        {
            if (audioFilePathsInFolder.Count == 0)
                return;
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Audio File to Row ID Mapping", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("フォルダ内の音声ファイルと、CSVのIDを紐づけてください。「Unassigned」のままのファイルは処理されません。", MessageType.Info);
            
            audioMapScrollPosition = EditorGUILayout.BeginScrollView(audioMapScrollPosition, GUILayout.Height(150));

            var audioPaths = new List<string>(audioFilePathsInFolder);
            foreach (var audioPath in audioPaths)
            {
                EditorGUILayout.BeginHorizontal();

                string fileName = Path.GetFileName(audioPath);
                
                // ラベル部分
                GUILayout.Label(fileName, GUILayout.ExpandWidth(true));
                
                // ドロップダウンリストを表示
                if (audioIdSelection.ContainsKey(audioPath))
                {
                    audioIdSelection[audioPath] = EditorGUILayout.Popup(audioIdSelection[audioPath], availableCsvIds.ToArray(), GUILayout.Width(150));
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.Space();
        }
    }
}
