      - name: Unduh WebView2 bootstrapper
        shell: pwsh
        run: |
          New-Item -ItemType Directory -Force dist\webview2 | Out-Null
          Invoke-WebRequest -Uri "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile dist\webview2\MicrosoftEdgeWebview2Setup.exe

      - name: Pasang Inno Setup
        run: choco install innosetup -y --no-progress

      - name: Buat installer
        shell: pwsh
        run: |
          $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
          & $iscc "/DAppVersion=1.0.${{ github.run_number }}" installer\guru.iss
          if ($LASTEXITCODE -ne 0) { exit 1 }
          & $iscc "/DAppVersion=1.0.${{ github.run_number }}" installer\client.iss
          if ($LASTEXITCODE -ne 0) { exit 1 }

      - name: Simpan hasil build
        uses: actions/upload-artifact@v4
        with:
          name: MARSA-Lab-Installer
          path: |
            out/MarsaLab-Guru-Setup.exe
            out/MarsaLab-Client-Setup.exe
          retention-days: 14