# ViewModels AutoTrading Refactor Stage6

## 목적
파일만 나누는 방식이 아니라, AutoTrading 카테고리 내부의 중복 책임을 실제로 통합했다.

## 정리 기준
- XAML 바인딩 public property 이름 유지
- public command 영향 없음
- 매매 판단 로직, 주문 수량 계산 로직 미변경
- 기존 csproj Compile Include 호환을 위해 빈 partial 파일 유지
- 새 기능 추가하지 않음

## 주요 변경
### MainWindowViewModel.AutoTrading.cs
- 자동매매 설정/상태 필드와 ON/OFF 라이프사이클 담당
- InitializeAutoTrading 정리
- StartAutoTradingForCurrentSymbol / StopAutoTradingForCurrentSymbol로 ON/OFF 분기 정리
- TiRex UI 갱신을 이 파일로 통합

### MainWindowViewModel.AutoTrading.State.cs
- Core 상태 동기화의 단일 진입점: SyncAutoTradeState
- Cash 전달 단일 진입점: UpdateAutoTradeCash
- 예수금 계산을 GetAutoTradeAvailableCash + GetPendingBuyAmount로 정리
- 보유수량/평단 조회 2중 메서드 제거 후 FindHolding + SyncAutoTradePosition으로 통합
- 분봉 조회를 보유주 저장소/현재창 버퍼별로 분리
- Clone helper 통합

### MainWindowViewModel.AutoTrading.Orders.cs
- 자동매매 주문 송신 흐름을 단계별 메서드로 정리
- ExecuteAutoTradeOrderRequest 내부 책임 축소
- 주문 전송 / 결과 로그 / 주문조회 요청 / 자동취소 요청 분리
- 자동취소 탐색 로직을 TryFindCancelableAutoTradeOrder로 정리

### MainWindowViewModel.AutoTrading.Analysis.cs
- 분석 저장 흐름을 FeatureSnapshot 저장과 SignalLog 저장으로 분리
- 긴 PersistAutoTradeAnalysis 메서드를 레코드 생성 메서드로 분해
- TradeCard 표시 보조 메서드 추가

## 빈 partial로 유지한 파일
- MainWindowViewModel.AutoTrading.TiRex.cs
- MainWindowViewModel.AutoTrading.Helpers.cs

위 파일들은 csproj 호환을 위해 남겨두었고, 활성 로직은 다른 파일로 이동했다.

## 주의
- 빌드는 확인 필요
- 외부 partial에서 GetHoldingQtyByCode / GetHoldingEntryPriceByCode를 직접 호출하고 있다면 빌드 오류가 날 수 있음
  - 이 경우 SyncAutoTradePosition 사용 또는 두 메서드를 wrapper로 재추가하면 됨
