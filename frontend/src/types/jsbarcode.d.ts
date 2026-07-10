// JsBarcode 앰비언트 타입 선언 — 로컬 번들(npm 의존성, 폐쇄망 안전)용 최소 타입.
//   · 라이브러리가 자체 .d.ts 를 제공하지 않아(@types 미도입) 사용 표면만 선언한다.
//   · module.exports = JsBarcode(함수) 형태(bin/JsBarcode.js) → default import 로 소비.
declare module 'jsbarcode' {
  /** JsBarcode 렌더 옵션(사용하는 필드만). 전체 옵션은 라이브러리 문서 참조. */
  export interface JsBarcodeOptions {
    /** 심볼로지(CODE128·CODE39·EAN13 …). */
    format?: string
    /** 모듈(가는 막대) 폭 px. */
    width?: number
    /** 막대 높이 px. */
    height?: number
    /** 사람이 읽는 값 텍스트 표시 여부. */
    displayValue?: boolean
    text?: string
    fontOptions?: string
    font?: string
    textAlign?: string
    textPosition?: string
    textMargin?: number
    fontSize?: number
    background?: string
    lineColor?: string
    margin?: number
    marginTop?: number
    marginBottom?: number
    marginLeft?: number
    marginRight?: number
    /** 유효성 콜백 — 제공 시 잘못된 값/형식에도 throw 하지 않고 valid(false) 호출. */
    valid?: (valid: boolean) => void
    flat?: boolean
  }

  /** 대상 엘리먼트(또는 셀렉터 문자열)에 text 를 심볼로지로 렌더. */
  function JsBarcode(element: Element | string, text: string, options?: JsBarcodeOptions): void

  export default JsBarcode
}
