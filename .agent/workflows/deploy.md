---
description: LuciLink 앱 빌드 및 GitHub Release 배포
---

// turbo-all

## 배포 단계

1. csproj에서 버전 올리기 (Version, AssemblyVersion, FileVersion)
   - 파일: `c:\project\Lucilink\LuciLink.Client\LuciLink.Client.csproj`
   - `<Version>`, `<AssemblyVersion>`, `<FileVersion>` 값을 새 버전으로 수정

2. Release 빌드
```powershell
dotnet publish LuciLink.Client/LuciLink.Client.csproj -c Release -r win-x64 --self-contained true -o publish
```

3. Velopack 패키징
```powershell
vpk pack --packId LuciLink --packVersion <NEW_VERSION> --packDir publish --mainExe LuciLink.exe --outputDir releases
```

4. Git commit & push
```powershell
git add -A; git commit -m "v<NEW_VERSION>: <변경사항 요약>"
git push origin main
```

5. GitHub Release 생성 및 에셋 업로드
```powershell
gh release create v<NEW_VERSION> --title "LuciLink v<NEW_VERSION>" --notes "<릴리스 노트>" releases/LuciLink-win-Setup.exe releases/LuciLink-win-Portable.zip releases/LuciLink-<NEW_VERSION>-full.nupkg releases/LuciLink-<NEW_VERSION>-delta.nupkg releases/RELEASES releases/assets.win.json releases/releases.win.json
```
