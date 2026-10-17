# Security policy

**English** · [한국어](#보안-정책)

KbmOverlay handles keyboard and mouse input, so please report security problems privately before making them public.

## Supported versions

Security fixes go into the [latest release](https://github.com/xyzw0128-debug/KbmOverlay/releases/latest) only.
Please check that the problem still happens there.

## Reporting a vulnerability

- Use the **Security** tab → **Report a vulnerability**. Only the maintainer can see the report.
- Please do not post it in public issues or discussions; others could abuse it before it is fixed.
- These details help:
  - KbmOverlay version and Windows version
  - one PC or two PCs (and on which PC it happens)
  - steps to reproduce, and what an attacker gains (for example: can read key input)

Reports are looked at as soon as possible. When a fixed version is released, you will be asked first whether you
want to be credited.

## What counts as a vulnerability

- Reading key or mouse input from outside this PC or from another website (including bypassing the Host and Origin
  checks on port 5520)
- On the two-PC connection (port 5521):
  - pairing without approval, or impersonating a paired PC
  - reading or changing the encrypted input
  - running code on, or crashing, the streaming PC just by connecting
- Firewall rules wider than documented in the README (KbmOverlay.exe only, port 5521, same network)
- Pairing secrets in the settings file becoming usable on another Windows account or PC

## Known limits (not vulnerabilities)

- **The exe is not code-signed.** Windows SmartScreen warns on first run. Check the exe with the SHA-256 in the
  release notes.
- **First pairing is only safe if you compare the codes.** If an attacker is on the same network and you allow
  without comparing, the connection could be intercepted.
- **The two-PC connection is encrypted, but *when* key events are sent is visible on the same network.**
- **Anyone who can use this PC with the same Windows account has the same rights as KbmOverlay.** They could read
  this PC's input directly, so KbmOverlay does not try to stop them.
- **Antivirus software may be suspicious of a program that reads key input.** Report false positives as a normal issue.

The design is described in the [README's Security section](README.md#security).

---

# 보안 정책

[English](#security-policy) · **한국어**

KbmOverlay는 키보드·마우스 입력을 다루는 프로그램이라, 보안 문제는 공개하기 전에 먼저 비공개로 알려 주세요.

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

설계는 [한국어 README의 보안 섹션](README.ko.md#보안)에 정리돼 있어요.
