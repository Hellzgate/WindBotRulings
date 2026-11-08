# ARCHITECTURE — WindBot 재정 검색기 (WindBotRulings)

> **수정 전에 이 문서만 읽으면 되도록** 정리한 구조/확장 안내서입니다.
> 대상 독자: 몇 달 뒤에 이 코드를 다시 열어 고치는 사람(미래의 나 포함).

---

## 0. 원칙 (지키면 안 깨짐)
1. **WinForms 단일 창** · 외부 라이브러리는 `Microsoft.Data.Sqlite` 하나뿐.
2. **UI(Program.cs)** 는 "표시"만, **데이터(RulingsCore.cs)** 는 "수집·캐시"만. 새 기능은 이 경계를 넘지 않게.
3. 수집은 **사용자가 버튼을 눌렀을 때만**(자동 대량 크롤링 금지) · 요청 간 **120ms** 지연.
4. 실패는 **조용히**(예외는 잡고 상태줄에만 표시), 시작 실패만 `startup-error.txt` + 메시지박스.
5. 검증은 항상 **`--uicheck` → `--selftest`** 두 개로.

---

## 1. 파일 지도

| 파일 | 클래스 | 책임 |
|---|---|---|
| `Program.cs` | `Program` | 진입점 · 인자 분기(`--selftest`, `--uicheck`) · 크래시 로그 · 전역 예외 처리 |
| 〃 | `Settings` | 사용자 설정(경로/다크모드/비율/최근/즐겨찾기) JSON |
| 〃 | `MainForm` | 창 전체(UI·이벤트·수집 호출) — 4개 `#region` |
| 〃 | `SelfTest` | 창 없이 코어 검증(`--selftest`) |
| `RulingsCore.cs` | `RulingsData` | 카드 1장의 수집 결과 DTO |
| 〃 | `RulingsStore` | `%LOCALAPPDATA%\WindBotAIEditor\rulings\<id>.json` 읽기/쓰기 |
| 〃 | `RulingsFetcher` | **모든 외부 소스**(YGOPRODeck·Yugipedia·공식 Q&A) + 텍스트 정리 |
| 〃 | `ScriptFacts` | 카드 Lua → 기계적 사실(효과 종류·제한·코스트·효과번호) |
| `CardDatabase.cs` | `CardDatabase` | cdb(SQLite) 읽기 — **원본은 에디터**(`WindBotAIEditor\Services`)를 링크 컴파일 |

### MainForm 내부 구역 (`#region`)
| region | 내용 |
|---|---|
| **UI 구성** | 컨트롤 생성·배치·이벤트 배선(생성자) · `MakeText`/`SetText` |
| **레이아웃·테마** | `ApplySplitRatio`(비율 배치) · `ApplyTheme`(다크/라이트) |
| **카드 검색·목록** | `OpenDb` · `InfoOf` · `DoSearch` · `ReloadList` · `AddRecent` · `ToggleFavorite` · `UpdateFavButton` |
| **재정 수집·표시** | `ClearView` · `BuildOfficialText` · `ShowSelected` · `SaveMemo` · `FetchAsync` |

### RulingsCore 내부 구역
| region | 내용 |
|---|---|
| **데이터·캐시** | `RulingsData` · `RulingsStore` |
| **소스 수집** | `Http`(UA 고정) · `ResolveEnglishNameAsync` · `FetchYugipediaAsync` · `ExtractCids` · `ParseRows` · `FetchOfficialAllAsync` · `FetchOfficialListAsync` · `FetchOfficialDetailAsync` · `ResolveJapaneseNameAsync` · `ExtractDivText` · `LooksLikeChrome` · `CleanWiki` · `StripHtml` |
| **스크립트 사실** | `FindLua` · `Tokens` · `Build` |

---

## 2. 데이터 모델 (`RulingsData`)
| 필드 | 의미 | 채우는 곳 |
|---|---|---|
| `Id` | 카드 ID | UI |
| `NameKr / NameEn / NameJa` | 한글(cdb) / 영문(YGOPRODeck) / 일본어(Yugipedia `ja_name`) | `RulingsStore`·`ResolveEnglishNameAsync`·`ResolveJapaneseNameAsync` |
| `FactsText` | 스크립트 사실 텍스트 | `ScriptFacts.Build` |
| `YugipediaText / YugipediaUrl` | 영문 재정 + 링크 | `FetchYugipediaAsync` |
| `OfficialText / OfficialUrl / OfficialCids` | 공식 Q&A 본문/링크/cid 목록 | `FetchOfficialAllAsync` |
| `Memo` | 사용자 한국어 메모 | UI |
| `FetchedAt` | 수집 시각 | UI |

---

## 3. 수집 흐름 (`FetchAsync` → 소스 체인)
```
[버튼] 온라인 수집
  └ (1) NameEn 없으면  RulingsFetcher.ResolveEnglishNameAsync(id)
  └ (2)                RulingsFetcher.FetchYugipediaAsync(NameEn)   → 영문 재정 + cids[]
  └ (3) cids 있으면     RulingsFetcher.FetchOfficialAllAsync(cid, null)
        없으면          NameJa = ResolveJapaneseNameAsync(NameEn)
                        RulingsFetcher.FetchOfficialAllAsync(0, NameJa)   ← 키워드 검색
  └ (4)                RulingsStore.Save(d)
  └ (5)                ShowSelected()  → 탭 4개 채우기
```
`FetchOfficialAllAsync` 내부:
```
목록 URL(ope=4&cid= 또는 ope=2&keyword=) → ParseRows(html) → [(fid, date, question)]
  → 각 fid: FetchOfficialDetailAsync(fid)  (120ms 지연, 최대 maxItems=30)
  → "── Qn / Q. / A. / 원문링크" 텍스트 조립
```

---

## 4. 확장 레시피

### A. 새 재정 소스(사이트) 추가
1. `RulingsCore.cs` 의 **소스 수집** region 에 `public static async Task<(string text,string url)> FetchXxxAsync(...)` 추가
   - 반드시 `try/catch` 로 감싸고 실패 시 `("", url)` 반환(예외 던지지 말 것)
2. `RulingsData` 에 필드 추가(`XxxText`, `XxxUrl`)
3. `MainForm.FetchAsync` 에서 (3) 단계 뒤에 호출 추가 + `RulingsStore.Save`
4. `ShowSelected` 에서 탭 하나 추가(또는 기존 탭에 이어붙이기)
5. 문서(이 파일 §3, `23-rulings-search-tool.md`) 갱신 + `--selftest` 로 확인

### B. 탭 추가
```csharp
var pNew = new TabPage("새 탭") { Padding = new Padding(2) };
pNew.Controls.Add(_txtNew);                 // MakeText() 로 생성
_tabs.TabPages.Add(pNew);                    // 순서 주의: ShowSelected 의 인덱스와 맞출 것
```
> `_btnOpenSrc` 는 **탭 인덱스(1=공식, 2=유기페디아)** 로 URL 을 고릅니다. 탭 순서를 바꾸면 여기도 수정.

### C. 파싱 규칙 수정 (사이트 개편 대응)
- 공식 목록: `ParseRows(html)` — 마커는 `<span class="name">` + `fid=` + `更新日:</span>`
- 공식 상세: `ExtractDivText(html, "question_text" | "answer_text")` — **영어 라벨** 기준
- Yugipedia: `CleanWiki` (ref/템플릿/링크 제거), cid 는 `ExtractCids`(원문에서 `?cid=`)
- 일본어명: `ResolveJapaneseNameAsync` — `ja_name` + `{{Ruby|표시|읽기}}`

### D. 번역 붙이기 (미구현 · 설계만 있음)
- 위치: `MainForm` 에 `번역` 버튼 → `RulingsCore` 에 `Task<string> TranslateAsync(string text, string provider)` 추가
- 캐시 키: `sha1(text)` → `%LOCALAPPDATA%\WindBotAIEditor\rulings\tr\<hash>.txt`
- 원칙: **버튼 눌렀을 때만**, 결과에 "기계번역" 라벨, 원문 병기

---

## 5. 캐시/설정 스키마
```
%LOCALAPPDATA%\WindBotAIEditor\rulings\
  <카드ID>.json      RulingsData (위 §2 필드 그대로, System.Text.Json)
  settings.json      EdoproRoot, LastSearch, DarkMode, SplitRatio, Recent[], Favorites[]
  selftest-last.txt  마지막 --selftest 로그
  startup-error.txt  시작 실패 스택(있을 때만)
```
- `Recent`: 앞이 최신, 최대 50 (`AddRecent`)
- `SplitRatio`: 0.25~0.72 밖이면 0.47 로 보정(`ApplySplitRatio`)

---

## 6. 함정 (실제로 겪은 것 — 반복 금지)
| 함정 | 규칙 |
|---|---|
| `SplitContainer` 의 `Panel1MinSize`/`Panel2MinSize`/`SplitterDistance` 를 **생성자에서** 설정 | 기본 폭 150px 라서 `InvalidOperationException` → **앱이 조용히 종료**. 반드시 `Shown` 이후 + 폭 가드(`usable > 260+470+40`) |
| WinForms `TextBox` 에 `\n` 만 넣기 | 줄바꿈 안 됨 → `SetText()` 로 `\r\n` 정규화. `WordWrap=true` |
| WinExe 콘솔 출력 | PowerShell `*>` 로는 안 잡힘 → `cmd /c "exe ... > file"` 사용 |
| 공식 페이지에서 `回答`(일본어) 검색 | 라벨은 **영어** `Question`/`Answer`, DOM 은 `div#question_text`/`answer_text` |
| `CardDatabase.cs` 를 같은 폴더에 복사하고 csproj 에도 `Compile Include` | SDK 자동 포함과 **중복** → `NETSDK1022`. 폴더 안에 두면 csproj 줄 삭제 |
| `--uicheck` 없이 GUI만 테스트 | 시작 예외는 창이 안 떠서 알 수 없음 → 항상 `--uicheck` 먼저 |
| 수집 텍스트 재배포 | 공식=Konami / Yugipedia=CC BY-SA → **캐시 폴더는 배포 금지** |

---

## 7. 검증 체크리스트 (수정 후 필수)
```powershell
dotnet build -c Release                        # ① 오류 0
.\bin\Release\net8.0-windows\WindBotRulings.exe --uicheck
                                               # ② "UICHECK OK" (폼 생성 예외 없음)
.\bin\Release\net8.0-windows\WindBotRulings.exe --selftest "D:\edopro" 14558127
                                               # ③ cdb→영문명→Yugipedia→cid→공식 Q&A→스크립트 전 구간
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o .\out
                                               # ④ 배포본에서 ② ③ 재확인
```
정상 기준(2026-10-29 실측)
```
14558127 하루 우라라 : Yugipedia 519자 · cid 1건 · 공식 Q&A 10건/5,868자 · 사실 216자
100458025 (커스텀)  : 일본어명 眠らない街の『レイズ・ムーン』 → 키워드 검색 1건/1,221자
```

---

## 8. 알려진 개선 후보
| # | 항목 | 메모 |
|---|---|---|
| 1 | **파일 분할**(Program.cs 654줄 / RulingsCore.cs 457줄 → 클래스별 파일) | 시도했으나 괄호/문자열 처리 리스크로 **보류**. `#region` 으로 구역 표시해 둠. 분할 시 `--uicheck`/`--selftest` 필수 |
| 2 | 번역 provider(§4-D) | 사용자 키(Papago/Azure) 또는 로컬 |
| 3 | 공식 Q&A 상세 페이지의 **답변 링크**(본문 내 카드 링크) 파싱 | 현재 텍스트만 |
| 4 | ~~최근/즐겨찾기 목록 **삭제**~~ | **v1.2 완료** — 우클릭 메뉴(목록에서 삭제 / 모두 지우기) |
| 5 | cdb가 여러 언어일 때 **한글명 우선순위** 설정 | `CardDatabase.FindCandidates` 사용 중 |
| 6 | 탭 제목에 수집 건수 표시 | 공식 Q&A 는 `[공식 Q&A] N건` 본문에 있음 — 파싱해 제목에 넣으면 좋음 |
| 7 | 번역된 재정(한국어) 요약 | 공식/영문을 요약 번역. 오역 위험이 있어 사용자 확인 후 |
| 8 | 최근 목록 **드래그 정렬** | 삭제(v1.2)는 완료, 순서 변경은 미구현 |

### 8-1. v1.2 에서 반영된 것 (2026-11-08)

| 항목 | 내용 |
|---|---|
| 데이터 폴더 | `<exe>\data` 우선(포터블) · 폴백 `%LOCALAPPDATA%\WindBotRulings` · 예전 `WindBotAIEditor\rulings` 자동 이전 |
| EDOPro 폴더 | 설정이 비었거나 사라지면 `D:\edopro`·`C:\ProjectIgnis` … 자동 탐색(`EdoproLocator.Guess`) |
| CLI | `--search <ID\|이름> [--fresh] [--json] [--root 폴더]` · `--version` · `--help` |
| 수집 절차 공용화 | `RulingsHarvest.FetchAsync` — UI/CLI 가 같은 순서(사실→영문명→Yugipedia→cid→일본어 키워드) |
| UI | **[복사]**(현재 탭 클립보드) · 우클릭 메뉴(즐겨찾기 토글/ID 복사/목록 삭제/모두 지우기) · **Ctrl+F** 검색 포커스 · **F5** 수집 · 툴바를 `FlowLayoutPanel` 로(좁은 창에서도 안 겹침) |

---

## 9. 관련 문서
- `README-DEV.md` (이 폴더) — 빌드/실행/배포
- `D:\rea-out\windbot-ai-editor\23-rulings-search-tool.md` — 소스 체인 실측·버그 이력
- `C:\Users\Main PC\Documents\기본 프로젝트\AGENTS.md` — 작업 규칙(검증·실측 보고)
