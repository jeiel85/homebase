# Homebase — 작업 지침

이 저장소에서 일하는 모든 세션(워크트리 포함)이 따르는 규칙. 설계 결정은 `DECISIONS.md`, 변경 내역은 `CHANGELOG.md`에 있다.

## 릴리스와 랜딩 페이지는 한 PR로 (사용자 지정, 2026-11-19)

GitHub Pages 랜딩 페이지(`docs/index.html`)는 **릴리스하는 PR 안에서 같이** 고친다. 배포가 끝난 뒤 랜딩만 따로 고치는 docs PR을 만들지 않는다.

- **언제**: `CHANGELOG.md`에 새 버전 절(`## vX.Y.Z — …`)을 더하는 PR, 즉 머지한 뒤 `vX.Y.Z` 태그를 달아 배포할 PR이면 항상.
- **무엇을** (`docs/index.html`):
  - 버전 표기 네 곳을 새 버전으로 바꾼다: 상단 칩 `<span data-version>vX.Y.Z</span>`, 휴대폰 화면 예시의 `Agent vX.Y.Z` 두 곳, 바닥글 `<span data-version>vX.Y.Z</span>`. 상단 칩은 페이지가 GitHub API로 최신 릴리스를 다시 읽으므로, 여기 적는 값은 API가 실패할 때의 기본값이다.
  - "최근 릴리스" 목록 맨 위에 새 버전 한 줄을 넣고, 맨 아래 가장 오래된 줄을 빼서 10개를 유지한다. 형식은 기존 줄과 같다: `<li><a href="https://github.com/jeiel85/homebase/releases/tag/vX.Y.Z"><b class="v">vX.Y.Z</b><time datetime="YYYY-MM-DD">MM.DD</time><span>한국어 한 줄 요약</span></a></li>`
  - 새 기능 때문에 랜딩의 기능 설명이 틀리게 되면 그 문구도 같은 PR에서 고친다.
- **버전 번호가 밀리면 랜딩도 같이**: 머지하기 전에 main에 다른 릴리스가 먼저 들어와 이 PR의 버전을 올리게 되면(CHANGELOG 절 이름을 바꿀 때), 랜딩의 버전 표기와 목록 줄도 같은 번호로 맞춘다. 먼저 나간 릴리스의 줄은 그 PR이 이미 넣었으니 충돌을 풀 때 둘 다 남긴다.
- **이유**: 배포 뒤에 랜딩을 따로 고치면 여러 세션이 같은 일을 겹쳐 하고(#89와 #90), 옛 main에서 만든 PR이 이미 반영된 다른 문구를 되돌릴 위험이 있으며, 그동안 랜딩이 배포와 어긋난 채 남는다.
- **참고**: Pages는 main에 머지되면 곧 배포되므로, 랜딩은 태그 릴리스가 끝나기 몇 분 전부터 새 버전을 보인다.
