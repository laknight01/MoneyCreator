# Orders Stage8 Refactor

## 목표
- 파일을 더 쪼개지 않고 주문 관련 중복 흐름을 기존 파일 안에서 통합.
- Chejan / OPW00007 / 미체결조회로 들어오는 주문 갱신 경로를 동일한 정규화/저장/동기화 흐름으로 통일.

## 주요 변경

### MainWindowViewModel.Orders.Events.cs
- `Kiwoom_OnOrdersReceived()`와 `Kiwoom_OnUnfilledOrdersReceived()`의 중복 정규화/저장/동기화 코드를 통합.
- 추가된 공통 흐름:
  - `IsOrderEventForCurrentWindow()`
  - `PrepareIncomingOrders()`
  - `NormalizeIncomingOrder()`
  - `ApplyOrdersToUi()`
  - `SaveOrdersAndBuildTradeResults()`

### MainWindowViewModel.Orders.Mapping.cs
- 주문 구분/정정/취소/잔량 계산 공통 helper 추가.
- 추가된 helper:
  - `RequestOrderStateRefresh()`
  - `IsBuyOrder()`
  - `IsSellOrder()`
  - `IsSellFilledOrder()`
  - `GetCancelOrderType()`
  - `GetModifyOrderType()`
  - `GetRemainingOrderQty()`
  - `ValidateOrderTarget()`

### MainWindowViewModel.Orders.cs
- 주문/정정/취소 후 주문내역/미체결 재조회 중복 호출을 `RequestOrderStateRefresh()`로 통합.
- 취소/정정 command의 공통 검증, 주문구분 코드 계산, 잔량 계산 중복 제거.

## 의도적으로 유지한 부분
- `AddOrUpdateOrderHistory()` 기존 병합 규칙은 외부 partial에 있을 수 있어 직접 변경하지 않음.
- XAML 바인딩 속성명/Command명 변경 없음.
- 매매 판단/주문 수량/DB 스키마 변경 없음.

## 다음 정리 후보
- `AddOrUpdateOrderHistory()` 실제 구현 위치 확인 후 Chejan/OPW00007 병합 규칙 단일화.
- `UntradedQty > 0` 기준으로 정정/취소 버튼 표시 상태를 명시적으로 유지.
