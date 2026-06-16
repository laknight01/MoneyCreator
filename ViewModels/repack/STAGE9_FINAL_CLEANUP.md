# ViewModels Final Stage9 Refactoring

## 목적
마지막 ViewModels 묶음(Account/Core/Performance/SymbolLoading/TradeAnalysisCard)을 대상으로
파일 추가 분리 없이 기존 파일 내부의 중복 경로와 종료/구독/보유종목 처리 흐름을 정리했다.

## 주요 변경

### MainWindowViewModel.Account.cs
- ApplyParentAccountContext 정리
  - CurrentBalance / AccountHoldings / OrderHistory 반영 후 UpdateAutoTradeCash 호출
  - 자동매매 예수금 계산이 OrderHistory 반영 전에 실행되는 문제 방지
- FetchAccountList 정리
  - 계좌 목록 Distinct 처리
  - Dispatcher 내부 로직 단순화
- UpdateBalance 정리
  - 사용하지 않는 isInitialLoading 변수 제거
  - WindowId 접근 통일
- RegisterApi null 방어 추가

### MainWindowViewModel.Core.cs
- MarketApi 이벤트 구독/해제 중복 제거
  - SubscribeMarketApiEvents()
  - UnsubscribeMarketApiEvents()
- AutoTradeController 종료 정리
  - DisposeAutoTradeController()
- 종료 시 DB flush/성과집계 정리
  - FlushDbOnClose()
- Cleanup / Dispose 중복 해제 로직 통합
  - Dispose()는 FlushDbOnClose → Cleanup → UnregisterWindow 순서로 정리

### MainWindowViewModel.SymbolLoading.cs
- LoadSymbolDataForCurrentWindowAsync 제거
  - LoadSymbolDataAsync(symbol, waitHoldingLoad:false)로 통합
- 보유종목 수신 처리 정리
  - UpdateAutoTradePositionsFromHoldings()
  - ApplyHoldingsToUi()
- Kiwoom_OnHoldingsReceived의 AutoTrade position 동기화와 UI 반영 책임 분리

### MainWindowViewModel.Performance.cs
- 기능 변경 없음
- 성과 집계 로직 안정 유지

### MainWindowViewModel.TradeAnalysisCard.cs
- 기능 변경 없음
- XAML 바인딩 속성 유지

## 의도적으로 유지한 부분
- public 바인딩 속성명 변경 없음
- Command 이름 변경 없음
- 매매 판단 / 주문 수량 / DB 저장 로직 변경 없음
- 파일 추가 분리 없음

## 빌드 확인 포인트
- LoadSymbolDataForCurrentWindowAsync 참조가 남아있는지 확인
- Cleanup/Dispose 중복 해제로 이벤트 누락 없는지 확인
- 자식창 계좌 컨텍스트 적용 후 자동매매 예수금 로그 확인
