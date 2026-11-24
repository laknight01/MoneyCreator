# MoneyCreator

**C# / WPF 기반 주식 자동매매 및 실시간 시장 데이터 분석 시스템**

MoneyCreator는 Kiwoom OpenAPI를 활용하여 실시간 주식 데이터를 수집하고, 시장의 거래 흐름과 가격 변화를 분석하여 매매 신호를 생성하고 주문을 실행하는 자동매매 시스템입니다.

단순한 가격 변동이나 개별 틱에 의존하는 방식이 아니라, 거래대금 기반 데이터 구조와 추세 분석, 피벗 검증을 결합하여 매매 판단의 신뢰도를 높이는 것을 목표로 개발하고 있습니다.

## 1. Development Environment

| 구분 | 기술 |
|---|---|
| Language | C# |
| UI Framework | WPF |
| Runtime | .NET Framework 4.8 |
| Analysis Library | .NET Standard 2.0 |
| API | Kiwoom OpenAPI |
| Database | Historical Market Data Storage |
| Chart | ScottPlot |
| Architecture | Modular Architecture |

## 2. System Architecture

각 모듈의 책임을 분리하여 UI, 데이터 수집, 매매 분석 및 주문 실행이 독립적으로 동작하도록 설계했습니다.

### MoneyCreator
- WPF 기반 사용자 인터페이스
- Kiwoom OpenAPI 연동 및 실시간 데이터 수집
- 계좌, 잔고, 주문 및 체결 상태 관리
- 자동매매 주문 실행
- 실시간 차트 및 분석 결과 시각화
- 과거 데이터 조회 및 재생 환경

### AutoTrading
- 실시간 시장 데이터 분석
- 매수·매도 신호 생성
- 추세 및 시장 방향 분석
- 피벗 기반 전환 구간 검증
- 매매 진입 및 청산 판단
- 주문 요청 데이터 생성

### SharedModel
- UI와 자동매매 엔진 간 데이터 공유
- 분석 결과 및 매매 신호 전달
- 모듈 간 데이터 구조 표준화

### TiRex
- 보조 데이터 저장 및 UI 활용
- 현재 매매 의사결정에는 직접 관여하지 않는 구조

## 3. Implemented Features

### 3.1 Real-time Market Data Collection
- Kiwoom OpenAPI 기반 실시간 주식 데이터 수집
- 체결 데이터 및 거래대금 분석
- 수집 데이터의 시간 순서와 상태 관리
- 실시간 데이터와 과거 데이터의 분리 및 관리

### 3.2 Dollar Bar Analysis

시간 기반 캔들의 한계를 보완하기 위해 거래대금 기반 데이터 구조를 구현했습니다.

- Total Dollar Bar
- Buy Dollar Bar
- Sell Dollar Bar
- 거래대금 누적에 따른 Bar 생성
- 매수·매도 방향별 거래 흐름 분석
- Total Dollar Bar를 기준으로 한 시장 데이터 검증

### 3.3 Trading Signal Engine

매매 판단을 여러 분석 단계로 분리하여 구성했습니다.

- 시장 상태 분석
- 매수·매도 압력 분석
- 거래대금 변화 및 가속도 분석
- 추세 방향 분석
- 매매 후보 검증
- 진입 및 청산 판단
- 분석 결과에 대한 점수화 및 상태 관리

### 3.4 Trend & Pivot Analysis

시장 방향과 가격 전환 구간을 분석하여 잘못된 방향의 진입을 줄이는 것을 목표로 합니다.

- 상승 및 하락 추세 분석
- 추세 방향에 따른 매매 필터링
- Pivot 후보 탐색
- 거래 흐름을 통한 Pivot 확정 검증
- 전환 구간의 방향 및 신뢰도 검증
- 과거 추세와 현재 시장 상태 비교

### 3.5 Automated Order Execution
- 분석 엔진과 주문 실행 모듈 분리
- 매매 신호 기반 주문 요청
- 계좌 및 보유 종목 상태 관리
- 주문 및 체결 상태 반영

### 3.6 Historical Data & Replay
- 과거 거래 데이터 저장 및 조회
- 날짜별 데이터 분석
- 과거 시장 데이터 재생
- 실시간 데이터와 과거 데이터의 비교 분석
- 전략 검증을 위한 재생 환경

## 4. Architecture & Design Principles

프로젝트 개발 과정에서 중요하게 고려한 설계 원칙입니다.

- UI와 매매 분석 로직의 책임 분리
- 데이터 수집과 주문 실행의 독립성 유지
- 매매 판단 단계의 모듈화
- 실시간 데이터와 분석 데이터의 명확한 구분
- 과거 데이터 기반 재현 및 검증 가능성
- 기존 매매 전략을 유지하면서 분석 기능을 확장할 수 있는 구조

## 5. Future Development Roadmap

### 5.1 Machine Learning Pipeline

기존의 수치 기반 매매 분석을 확장하여 가격 움직임을 사전에 예측하는 머신러닝 모델을 개발할 계획입니다.

개발 단계:

1. 실시간 틱 데이터 수집 및 학습 데이터 구축
2. 가격 움직임에 따른 매수·매도 결과 라벨링
3. 진입 시점별 이후 가격 변화 분석
4. 학습 데이터셋 생성 및 전처리
5. 머신러닝 모델 학습
6. 학습에 사용하지 않은 과거 데이터로 예측 성능 검증

### 5.2 Predictive Market Analysis

- 가격 상승 및 하락 가능성 예측
- 추세 전환 가능성 사전 탐지
- 매수·매도 진입 시점 예측
- 기존 분석 엔진과 예측 모델의 결과 비교
- 예측 결과를 활용한 매매 판단 개선

### 5.3 Strategy Validation

- 과거 데이터 기반 반복 검증
- 예측 시점과 실제 가격 변화 비교
- 잘못된 방향의 예측 및 진입 분석
- 신호 발생 시점과 실제 가격 전환 시점의 차이 분석
- 검증 결과를 통한 모델 및 전략 개선

## 6. Development Goal

최종 목표는 과거 가격 변화에 반응하는 분석 시스템을 넘어, 시장의 거래 흐름을 기반으로 앞으로 발생할 수 있는 가격 변화를 예측하는 시스템으로 발전시키는 것입니다.

기존의 규칙 기반 분석 엔진과 머신러닝 기반 예측 모델을 결합하여 보다 빠르고 신뢰도 높은 매매 판단 구조를 구축하는 것을 목표로 합니다.

---

**Project:** MoneyCreator  
**Language:** C#  
**Framework:** WPF / .NET Framework 4.8  
**API:** Kiwoom OpenAPI  
**Repository:** https://github.com/laknight01/MoneyCreator
