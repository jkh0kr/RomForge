# 3DS - 설치 - 신규 게임 설치

3DS 실기로 직접 게임을 설치하면 매우 느리기 때문에, PC에서 SD 카드에 바로 게임을 설치하는 탭입니다.

!!! danger "키셋 + movable.sed 필수"
    이 기능은 3DS 키셋과 `movable.sed` 파일이 반드시 있어야 동작합니다.

    `movable.sed`는 SD 카드 루트 또는 `/gm9/out/` 하위에 복사해두면 자동으로 인식됩니다.

## 화면 구성

1. **SD 카드**
2. **movable.sed** — 클릭하면 [seedminer.hacks.guide](https://seedminer.hacks.guide/) 사이트로 이동
3. **movable.sed 선택**
4. **게임 파일 리스트**
5. **설치**
6. **취소**
7. **진행도**
8. **로그**

<img width="850" height="683" alt="1783836554" src="https://github.com/user-attachments/assets/2a5715a7-ba10-4c62-9c17-cc1ae0f15c7d" />

---

## 사용 예시

3DS 자체 설치 기능(FBI 등)은 매우 느려서, 게임을 여러 개 설치하려면 설치를 걸어놓고 오래 기다려야 합니다. 게다가 도중에 오류가 나면 파일을 다시 구해서 재시도해야 하는 번거로움도 있습니다.

이럴 때 이 기능을 사용하면 SD 카드 최대 속도로 게임을 설치할 수 있어 시간을 크게 절약할 수 있습니다.

!!! danger "설치 후 반드시 실행할 것"
    설치가 끝난 뒤, 3DS 실기에서 홈브류 런처로 `SD루트\3ds\custom-install-finalize.3dsx` 파일을 반드시 실행해야 3DS 홈 화면에 게임이 정상적으로 추가됩니다.

    게임 설치 후 이 파일이 SD 카드에 없으면 RomForge가 자동으로 복사해 줍니다.
