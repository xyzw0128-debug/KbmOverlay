# 보안 정책

KbmOverlay는 키보드·마우스 입력을 다루는 프로그램이라, 보안 문제는 공개 이슈보다 먼저 비공개로 알려 주세요.

## 지원하는 버전

보안 수정은 [최신 릴리스](https://github.com/xyzw0128-debug/KbmOverlay/releases/latest)에만 해요.
문제가 있으면 먼저 최신 버전에서도 같은지 확인해 주세요.

## 신고 방법

- 저장소의 **Security** 탭 → **Report a vulnerability**로 알려 주세요. 신고 내용은 관리자에게만 보여요.
- 공개 이슈나 토론 글에는 올리지 말아 주세요. 고치기 전에 다른 사람이 악용할 수 있어요.
- 이런 내용이 있으면 확인이 빨라요.
  - KbmOverlay 버전과 Windows 버전
  - 원컴인지 투컴인지 (투컴이면 어느 PC에서 생긴 일인지)
  - 재현 방법, 그리고 공격자가 얻는 것 (예: 키 입력을 읽을 수 있다)

받은 신고는 가능한 한 빨리 확인하고, 고친 버전을 릴리스할 때 신고자를 밝혀도 되는지 먼저 물어볼게요.

## 보안 문제로 보는 것

- 이 PC 밖이나 다른 웹사이트에서 키·마우스 입력을 읽을 수 있는 경우 (포트 5520의 Host·Origin 검사를 우회하는 경우 포함)
- 투컴 연결(포트 5521)에서
  - 허용 없이 짝을 맺거나 짝을 맺은 PC를 흉내 낼 수 있는 경우
  - 암호화된 입력을 읽거나 바꿀 수 있는 경우
  - 연결만으로 방송 PC에서 코드를 실행하거나 프로그램을 멈출 수 있는 경우
- 방화벽 규칙이 README에 적힌 범위(KbmOverlay.exe 하나, 포트 5521, 같은 네트워크)보다 넓게 열리는 경우
- 설정 파일의 짝 비밀값이 다른 Windows 계정이나 다른 PC에서 풀리는 경우

## 알려진 한계 (취약점이 아니에요)

- **exe에 코드 서명이 없어요.** 처음 실행할 때 Windows SmartScreen 경고가 나와요.
  받은 exe가 맞는지는 릴리스 설명의 SHA-256으로 확인할 수 있어요.
- **처음 짝을 맺는 순간은 사용자가 확인 숫자를 비교해야 안전해요.** 같은 네트워크에 공격자가 있을 때
  숫자를 비교하지 않고 허용하면 중간에서 가로챌 수 있어요.
- **투컴 연결의 내용은 암호화되지만, 키 이벤트가 *언제* 오가는지는 같은 네트워크에서 볼 수 있어요.**
- **같은 Windows 계정으로 이 PC를 쓸 수 있는 사람은 KbmOverlay와 같은 권한을 가져요.**
  그 사람은 이 PC의 입력을 직접 읽을 수도 있으므로 KbmOverlay가 막는 대상이 아니에요.
- **키 입력을 읽는 구조상 백신이 의심할 수 있어요.** 오탐으로 보이면 일반 이슈로 알려 주세요.

설계는 [README의 보안 섹션](README.md#보안)에 정리돼 있어요.

---

## Security policy (English)

Please report vulnerabilities privately through the **Security** tab → **Report a vulnerability**, not in public issues.
Only the latest release receives security fixes. In scope: reading input from outside this PC or from other websites,
pairing or impersonating a PC without approval, reading or altering relay traffic, crashing or running code on the
streaming PC over port 5521, and firewall rules wider than documented. Known limits that are not vulnerabilities are
listed above (unsigned exe, first pairing relies on comparing the code, event timing is visible on the LAN, local users
of the same Windows account, antivirus false positives).
