import sys
import json
import speech_recognition
import difflib
from pydub import AudioSegment
import os

def batch_process(tasks_json, engine, language, model):
    try:
        # BOM(U+FEFF)を先頭から除去してからJSONをパースする
        if tasks_json.startswith('\ufeff'):
            tasks_json = tasks_json.lstrip('\ufeff')

        tasks = json.loads(tasks_json)['tasks']
        results = []

        recognizer = speech_recognition.Recognizer()

        total_tasks = len(tasks)
        # デバッグ用に標準エラー出力へ進捗を出力
        print(f"Total tasks received: {total_tasks}", file=sys.stderr)

        for i, task in enumerate(tasks):
            task_id = task['id']
            audio_path = task['audioPath']
            script_text = task['scriptText']

            print(f"Processing {i + 1}/{total_tasks}: {os.path.basename(audio_path)}", file=sys.stderr)

            recognized_text = ""
            error = None
            diff = []
            similarity = 0
            temp_wav_path = None

            try:
                temp_wav_path = os.path.splitext(audio_path)[0] + "_temp.wav"
                sound = AudioSegment.from_file(audio_path)
                sound.export(temp_wav_path, format="wav")

                with speech_recognition.AudioFile(temp_wav_path) as source:
                    audio_data = recognizer.record(source)
                    if engine == "whisper":
                        recognized_text = recognizer.recognize_whisper(audio_data, model=model, language=language)
                    elif engine == "google":
                        recognized_text = recognizer.recognize_google(audio_data, language=language)
                    elif engine == "sphinx":
                        recognized_text = recognizer.recognize_sphinx(audio_data, language=language)
                    else:
                        raise ValueError(f"Unsupported engine: {engine}")

                similarity = difflib.SequenceMatcher(None, script_text, recognized_text).ratio()
                diff = list(difflib.unified_diff(
                    script_text.splitlines(), recognized_text.splitlines(),
                    fromfile="CSV Script", tofile="Recognized Audio", lineterm=""
                ))

            except Exception as e:
                error = str(e)
                print(f"Error processing task {task_id}: {e}", file=sys.stderr) # エラー詳細を標準エラー出力へ

            finally:
                if temp_wav_path and os.path.exists(temp_wav_path):
                    os.remove(temp_wav_path)

            results.append({
                "id": task_id,
                "similarity": similarity,
                "script_text": script_text,
                "recognized_text": recognized_text,
                "diff": diff,
                "error": error
            })

        # 正常終了時の結果を標準出力へ
        print(json.dumps({"results": results}, ensure_ascii=False))

    except Exception as e:
        # print関数の引数の誤りを修正
        error_output = {"results": [], "error": f"Batch process failed: {e}"}
        print(json.dumps(error_output, ensure_ascii=False))

if __name__ == "__main__":
    try:
        # C#から渡されるコマンドライン引数を受け取る
        engine_arg = sys.argv[1]
        language_arg = sys.argv[2]
        model_arg = sys.argv[3]
        # 5番目の引数として「一時ファイルのパス」を受け取る
        tasks_file_path = sys.argv[4]

        # 受け取ったファイルパスからJSONデータを読み込む
        with open(tasks_file_path, 'r', encoding='utf-8-sig') as f:
            tasks_json_arg = f.read()

        # 読み込んだJSONデータを使ってバッチ処理を実行
        batch_process(tasks_json_arg, engine_arg, language_arg, model_arg)

    except IndexError:
        # 引数が足りない場合のエラー処理
        error_output = {"results": [], "error": "Invalid arguments. Expected: engine, language, model, filepath"}
        print(json.dumps(error_output, ensure_ascii=False))
    except Exception as e:
        # その他の予期せぬエラー
        error_output = {"results": [], "error": f"An unexpected error occurred in Python script: {e}"}
        print(json.dumps(error_output, ensure_ascii=False))
