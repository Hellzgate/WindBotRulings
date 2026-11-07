# WindBot 재정 검색기 — 개발자용 소스 (WindBotRulings)

> 카드 **재정(룰링)** 을 조회하는 **단독 WinForms 프로그램** 소스입니다.
> 이 폴더만 있으면 **독립적으로 빌드/실행**됩니다(외부 프로젝트 링크 없음).

---

## 1. 요구 사항
| 항목 | 값 |
|---|---|
| OS | Windows 10/11 |
| SDK | **.NET 8 SDK** (`dotnet --version` 확인) |
| 패키지 | `Microsoft.Data.Sqlite` 10.0.14 (csproj 에 선언, 첫 빌드 시 자동 복원) |
| 런타임 데이터 | **EDOPro 폴더** (`<EDOPro>\cards.cdb` 또는 `repositories\**\*.cdb`, `script\**\c<ID>.lua`) |

---

## 1-1. 먼저 읽을 것
> **구조·확장 절차·함정**: `ARCHITECTURE.md` (같은 폴더) — 수정 전에 이 문서부터.

## 2. 파일 구성
| 파일 | 역할 |
|---|---|
| `Program.cs` | WinForms UI (검색·목록·탭 4개·툴바·다크모드·최근/즐겨찾기·`--selftest`/`--uicheck`) |
| `RulingsCore.cs` | 데이터 계층 — 소스 체인(공식 Q&A·Yugipedia·YGOPRODeck·스크립트 사실), 캐시, HTML/위키 파서 |
| `CardDatabase.cs` | cdb(SQLite) 읽기 — 원본은 `WindBotAIEditor\Services\CardDatabase.cs` (여기로 **복사**해 독립화) |
| `WindBotRulings.csproj` | net8.0-windows / WinForms / 패키지 참조 |
| `app.manifest` | Windows 10/11 호환 선언 |

---

## 3. 빌드 · 실행
```powershell
# 빌드
dotnet build -c Release

# 실행 (GUI)
dotnet run -c Release

# 창 없이 폼 생성만 검사 (Startup 예외 검출)
dotnet run -c Release -- --uicheck
# → "UICHECK OK" / 실패 시 "%LOCALAPPDATA%\WindBotAIEditor\rulings\startup-error.txt"

# 수집 경로 검사 (cdb→영문명→Yugipedia→cid→공식 Q&A→스크립트)
dotnet run -c Release -- --selftest "D:\edopro" 14558127

# 단일 실행 파일 배포
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o .\out
```

---

## 4. 데이터 흐름 (소스 체인)
```
카드ID → cdb(한글명)
       → YGOPRODeck API            (영문명)
       → Yugipedia Card Rulings    (영문 재정 + 공식 Q&A cid 인용)
       → 공식 Q&A  ope=4&cid=…     (카드별 목록)  ┐
         또는     ope=2&keyword=…  (일본어명 검색) ├→ ope=5&fid=… 상세에서
       → div#question_text / div#answer_text     ┘   질문·답변 파싱
       → 없으면 스크립트 사실(Lua) + 내 메모
```
| 단계 | 엔드포인트/파일 | 비고 |
|---|---|---|
| 영문명 | `https://db.ygoprodeck.com/api/v7/cardinfo.php?id=<ID>` | JSON |
| Yugipedia | `https://yugipedia.com/api.php?action=parse&prop=wikitext&page=…` | MediaWiki API |
| 일본어명 | Yugipedia 카드 페이지 `ja_name` (`{{Ruby|표시|읽기}}` 제거) | |
| 공식 목록 | `faq_search.action?ope=4&cid=<cid>` / `ope=2&keyword=<일본어>` | **서버 렌더링** |
| 공식 상세 | `faq_search.action?ope=5&fid=<fid>` | `div#question_text` / `div#answer_text` |
| 스크립트 사실 | `<EDOPro>\script\**\c<ID>.lua` | `EFFECT_TYPE_*`, `SetCountLimit`, `SetDescription` |

---

## 5. 캐시/설정 (개인 로컬, 배포 금지)
```
%LOCALAPPDATA%\WindBotAIEditor\rulings\
  <카드ID>.json         카드별 수집 결과(질문/답변/영문/일본어/메모)
  settings.json         EDOPro 경로 · 검색어 · 다크모드 · splitRatio · recent · favorites
  selftest-last.txt     마지막 --selftest 로그
  startup-error.txt     시작 실패 스택(있을 때만)
```
> ⚠ 수집한 **재정 텍스트**(공식=Konami, Yugipedia=CC BY-SA)는 **저작물**입니다.
> 코드는 자유롭게 공유해도 되지만 **캐시 폴더는 배포물에 넣지 마세요.**

---

## 6. 수정 시 주의 (실제로 겪은 함정)
| 함정 | 규칙 |
|---|---|
| **SplitContainer 최소크기/거리** | 생성자에서 설정하면 기본 폭 150px 라서 **예외 → 앱이 조용히 종료**. 반드시 `Shown` 이후(실제 크기 확정) + 폭 가드 |
| **WinForms TextBox 줄바꿈** | `\n` 만으로는 안 됨 → `\r\n` 로 정규화해서 넣을 것 |
| **WinExe 콘솔 출력** | 리다이렉트(`cmd /c … > file`)로만 캡처됨. PowerShell `*>` 는 안 잡힘 |
| **공식 페이지 파싱** | 라벨이 **영어**(`Question`/`Answer`)이고 `div#question_text`/`answer_text`. 일본어 `回答` 로 찾으면 못 찾음 |
| **요청 예절** | 상세 조회는 120ms 간격, 카드당 최대 30건. 대량 크롤링 금지 |
| **cdb** | `Microsoft.Data.Sqlite` 필요. 파일이 여러 개면 `CardDatabase.FindCandidates` 우선순위 사용 |

---

## 7. 관련 문서
- 설계·실측·버그 이력: `D:\rea-out\windbot-ai-editor\23-rulings-search-tool.md`
- 작업 규칙(검증·실측 보고): `C:\Users\Main PC\Documents\기본 프로젝트\AGENTS.md`

