@echo off
rem 調査用: WebView2 のリモートデバッグを有効にして ChBrowser (Debug) を起動します。
rem 症状 (クリック無反応) が再発したらこのまま報告してください。外部からは localhost:9333 のみで接続します。
set WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333
start "" "C:\work\project\chbrowser\src\ChBrowser\bin\Debug\net8.0-windows\ChBrowser.exe"
