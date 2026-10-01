# Resenz

Windows용 [mihomo](https://github.com/MetaCubeX/mihomo) (Clash.Meta) GUI 클라이언트입니다. WinUI 3 / .NET 10 기반으로 만들었으며, 한국어 인터페이스를 기본으로 제공합니다.

![Version](https://img.shields.io/badge/version-0.6.0-blue) ![Platform](https://img.shields.io/badge/platform-Windows%2010%2B-lightgrey) ![UI](https://img.shields.io/badge/UI-%ED%95%9C%EA%B5%AD%EC%96%B4-red)

## 주요 기능

### 프록시 제어
- **실행 모드** — 규칙 / 전역 / 직접 모드를 제목 표시줄·트레이·단축키에서 전환
- **시스템 프록시** — 수동 모드와 PAC 모드 지원 (내장 로컬 PAC 서버)
- **TUN 모드** — 가상 네트워크 어댑터로 전 트래픽 캡처 (관리자 권한, 전환 시 코어 자동 재시작)
- **프록시 그룹·노드** — 그룹별 노드 선택이 기억되고, 코어 재시작 후 자동 복원
- **지연 측정** — 노드 단일/전체 측정, 네트워크 대상 측정, 측정 결과 전 패널 동기화

### 구독 관리
- 다중 구독: URL 또는 로컬 YAML 파일로 가져오기
- 구독 자동 갱신 (매시간), 사용량·만료일 표시 (`Subscription-Userinfo`)
- 구독 내 장애 조치 / 로드 밸런싱 / 체인 프록시 등 mihomo 그룹 기능 그대로 사용

### SSID 연동
- WiFi별 자동 전환: 지정 SSID에 연결되면 해당 구독으로 자동 교체
- WiFi별 일시중지: 집처럼 프록시가 필요 없는 네트워크에서 시스템 프록시 자동 해제

### 트래픽 모니터링
- 실시간 업/다운 속도, 세션 누적, 분별 기록 기반 트래픽 히스토리 (6시간~30일)
- 트래픽 랭킹: 호스트 / 프로세스 / 출처별 집계, 클릭으로 드릴다운
- 활성 연결 목록: 정렬·필터, 한 번에 mihomo 규칙으로 복사

### 편의 기능
- **미니 패널** — 항상 위 소형 창, 드래그 제스처로 접기 지원
- **트레이** — 상태 아이콘(실행/정지), 빠른 토글 메뉴
- **전역 단축키** — 모드 전환, TUN, 시스템 프록시, 미니 패널 등 (충돌 감지 내장)
- **테마** — 다크/라이트, Mica / 아크릴 / 단색 배경 소재, 탐색 모음 위치 선택
- **자동 업데이트** — 앱 자가 업데이트와 mihomo 코어 업데이트(버전 선택) 분리 제공
- 실시간 코어 로그 (최대 1,000줄, 레벨 필터), DNS·FakeIP 캐시 정리

## 시스템 요구 사항

- Windows 10 이상 (Windows 11 권장 — DWM 모서리·Mica 등 일부 효과는 Windows 11 전용)
- **TUN 모드는 관리자 권한 필요** — 앱이 관리자 권한으로 실행되면 코어가 관리자 토큰을 상속해 TUN이 바로 적용됩니다
- x86 / x64 / ARM64 지원

## 프로젝트 구조

```
src/
├── Resenz.App        WinUI 3 프레젠테이션 계층 (페이지, 뷰모델, 트레이, 미니 패널, 단축키)
├── Resenz.Core       코어 제어·서비스 계층 (mihomo 프로세스/API, 설정 생성, 시스템 프록시,
│                     구독, SSID 감지, 지연 측정, 업데이트, PAC 서버, 다운로드 프록시)
├── Resenz.Data       설정·데이터 영속화
└── Resenz.Elevated   TUN용 관리자 권한 도우미 (mihomo를 상승 권한으로 구동)
```

- 코어는 `mihomo.exe`, `wintun.dll`, 지리 데이터(GeoIP/GeoSite/MMDB)를 데이터 디렉터리에서 관리하며
  앱과 별도로 업데이트할 수 있습니다.
- 코어 프로세스를 직접 다루지 않고 mihomo RESTful API로 제어합니다.

## 빌드

- Visual Studio 2022 + **.NET 데스크톱 개발**, **WinUI 애플리케이션 개발** 워크로드
- .NET 10 SDK (`net10.0-windows10.0.26100.0`)

```bash
dotnet publish src/Resenz.App -c Release -r win-x64 -p:Platform=x64
```

릴리스 빌드는 GitHub Actions(`.github/workflows/release.yml`)가 처리합니다.
`v*` 태그 푸시 또는 수동 실행으로 x64/x86/ARM64 × slim/full 매트릭스가 빌드되고,
Inno Setup 설치판과 포터블 zip이 릴리스에 첨부됩니다.

## 참고

- 코어 엔진인 [mihomo](https://github.com/MetaCubeX/mihomo)와 [wintun](https://www.wintun.net/)은 각 저작권자의 라이선스를 따릅니다.
- 앱 아이콘은 [Iconify](https://icon-sets.iconify.design/)의 MIT 라이선스 픽셀 아이콘을 수정해 사용했습니다. 자세한 내용은 [src/Resenz.App/Assets/ICONS-LICENSE.md](src/Resenz.App/Assets/ICONS-LICENSE.md)를 참고하세요.
- 프록시 도구 특성상 각 지역·기관의 정책과 법규를 준수해 사용하시기 바랍니다.
