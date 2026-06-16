# MainWindowViewModel.MarketData 리팩토링 Stage7

## 목적
파일을 더 쪼개지 않고, 기존 MarketData 계열 파일 내부의 중복 처리 흐름을 줄였다.

## 정리 방향
- 새 기능 추가 없음
- 매매 판단 로직 변경 없음
- 차트/분봉/틱 처리 흐름 유지
- 공통 helper는 기존 `MainWindowViewModel.MarketData.cs` 안에 배치
- Ticks/Holdings/Minutes의 중복 코드 일부 통합

## 주요 변경

### MainWindowViewModel.MarketData.cs
공통 helper 추가:
- `IsCurrentWindowTick(...)`
- `UpdateCurrentSymbolFromTick(...)`
- `UpdateAccountHoldingPrice(...)`
- `BufferRealtimeTick(...)`
- `GetLatestCurrentMinuteForChart_NoLock()`
- `GetOrderedValidTicks(...)`
- `GetLatestTickTimeOrNow(...)`

### MainWindowViewModel.MarketData.Ticks.cs
- `Kiwoom_OnTickReceived()` 내부의 Context 검사/가격 갱신/실시간 버퍼 저장/최신 분봉 추출 로직을 helper로 통합
- 최근틱 정렬 필터링을 `GetOrderedValidTicks()`로 통합
- 최신 tick 시간 계산을 `GetLatestTickTimeOrNow()`로 통합
- 분 단위 시간 생성은 `ToMinuteTime()` 사용

### MainWindowViewModel.MarketData.Holdings.cs
- 보유주 TickFrame trim 로직을 기존 `TrimTickFrames_NoLock()`으로 통합
- 보유주 실시간 가격 갱신 시 `UpdateCurrentSymbolFromTick()` 사용

### MainWindowViewModel.MarketData.Minutes.cs
- 실시간 분봉 생성 시 분 단위 시간 생성 로직을 `ToMinuteTime()`으로 통일

## 의도적으로 유지한 부분
- `UpsertMinuteCandle_NoSort` / `UpsertTrackedMinute_NoSort`는 타입이 달라 무리하게 generic 통합하지 않음
- 분봉/틱 저장 구조 및 정렬 정책은 유지
- 차트 refresh 시점 유지
- AutoTradeCore 연동 시점 유지

## 적용 후 확인 포인트
1. 현재창 실시간 틱 수신 후 차트 갱신
2. 보유주 실시간 틱 수신 후 차트 갱신
3. 최근틱 보강 완료 후 SeedAutoTradeHistory 호출
4. 분봉 완료 후 차트 전체 바인딩
5. 보유주 분봉 완료 후 상태 표시
