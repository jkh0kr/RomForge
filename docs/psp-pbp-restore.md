# PSP - PBP 언팩

PBP 파일을 원본 BIN + CUE로 복원하는 탭입니다.

## 화면 구성

1. **게임 파일 리스트**
2. **파일 추가**
3. **폴더 추가**
4. **선택 삭제**
5. **전체 삭제**
6. **시작**
7. **취소**
8. **로그**

<img width="850" height="683" alt="1783998164" src="https://github.com/user-attachments/assets/04ef2109-c83d-4bd8-afef-e99bdb9b8776" />

---

## 사용 예시

PBP로 패킹한 뒤 원본 파일을 삭제해버린 경우 사용합니다.

!!! tip "다른 툴 대비 개선된 점"
    기존 툴들(PSX2PSP, PsxPackagerGUI)은 언패킹할 때 ISO9660 표준의 PVD 내부 **Volume Space Size** 값을 기준으로 언패킹합니다. 이 방식은 일부 한글패치가 적용된 게임을 복원할 때 파일이 깨지는 문제가 있는데, RomForge에서는 이 부분을 보완했습니다.

    다른 툴(PSX2PSP, PsxPackagerGUI)로 복원할 경우, 한글패치로 인해 파일 크기가 원본보다 커진 부분은 잘려나갈 수 있으니 주의하세요.
