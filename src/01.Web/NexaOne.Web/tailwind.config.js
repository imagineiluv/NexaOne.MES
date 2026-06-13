/** @type {import('tailwindcss').Config} */
module.exports = {
  // .razor의 class 사용을 스캔해 필요한 유틸리티만 생성
  content: ['./**/*.razor', './**/*.cshtml', './wwwroot/index.html'],
  // 전환기: preflight(전역 CSS 리셋) 비활성 — 기존 Bootstrap/DevExpress 스타일을 깨지 않는다.
  // DevExpress 완전 제거 후 true로 되돌리면 일관된 기준 스타일을 얻는다.
  corePlugins: { preflight: false },
  theme: { extend: {} },
  plugins: [],
}
