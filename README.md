# VoiceoverValidationTool_For_Unity

このエディタ拡張は、音声ファイルと台本（CSV）を音声認識で照合し、セリフの収録内容が正しいかを確認するためのツールです。

This editor extension is a tool for verifying the accuracy of recorded dialogue by collating audio files with a script (in CSV format) using speech recognition.

## 使い方

1. DialogueCheckerWindow.csをAssets以下のEditorフォルダに配置する。
2. RecognizeAndCompare.pyをAssets以下のPythonフォルダに配置する。
3. Unity Editorの画面上部のタブより、Toolsを選択する。
4. Dialogue Checkerをクリックし、ウィンドウを開く。
5. ウィンドウ上部から、pythonの実行ファイルの位置を指定する。
6. 次に音声認識エンジンを選択する。
7. 次に言語コードを選択する。
8. 次にCSVファイルのパスを指定する。
9. 次に音声ファイルが配置されている、フォルダを指定する。
10. すると、CSVの1列目に対応したドロップダウンが表示される。
11. CSVのセリフと対応する行のドロップダウンを選択する。
12. セリフが記載されている列のヘッダーを記載する。
13. すべて記入したうえで、Compare All Files in Editorをクリックすると類似度が計算される。

## 入力例
<img width="1549" height="947" alt="image" src="https://github.com/user-attachments/assets/3c7ea186-d190-4fc0-8bbf-e304f19e018f" />
