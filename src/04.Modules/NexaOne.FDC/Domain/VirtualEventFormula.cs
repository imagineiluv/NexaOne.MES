using NexaOne.Common;

namespace NexaOne.FDC.Domain;

/// <summary>가상 이벤트 조건 수식 평가기(V067 CONDITION_FORMULA). 문법(대소문자 무시):
/// <c>expr := term (OR term)* / term := factor (AND factor)* / factor := '(' expr ')' | comparison
/// / comparison := operand op operand / op := &gt;= &lt;= != == = &gt; &lt; / operand := 숫자 | 파라미터ID</c>.
/// 파라미터 ID는 최신 수집 값 딕셔너리에서 해석한다 — 값이 없는 파라미터 참조는 실패(Result)로 보고해
/// 엔진이 '조용히 false'로 오판하지 않게 한다. 재귀 하강 파서(외부 의존 없음, 식 트리/컴파일 없음).</summary>
public static class VirtualEventFormula
{
    public static Result<bool> Evaluate(string? formula, IReadOnlyDictionary<string, decimal> latestValues)
    {
        if (string.IsNullOrWhiteSpace(formula))
            return Result.Failure<bool>(Error.Validation("VirtualEvent.FormulaRequired", "조건 수식이 비어 있습니다."));

        try
        {
            var parser = new Parser(Tokenize(formula), latestValues);
            var value = parser.ParseExpression();
            parser.ExpectEnd();
            return Result.Success(value);
        }
        catch (FormulaException ex)
        {
            return Result.Failure<bool>(Error.Validation("VirtualEvent.FormulaInvalid", ex.Message));
        }
    }

    private sealed class FormulaException : Exception
    {
        public FormulaException(string message) : base(message) { }
    }

    private readonly record struct Token(string Kind, string Text);   // Kind: id|num|op|lparen|rparen|and|or

    private static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < input.Length)
        {
            var c = input[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '(') { tokens.Add(new("lparen", "(")); i++; continue; }
            if (c == ')') { tokens.Add(new("rparen", ")")); i++; continue; }
            if (c is '>' or '<' or '=' or '!')
            {
                var two = i + 1 < input.Length && input[i + 1] == '=';
                var op = two ? input.Substring(i, 2) : c.ToString();
                if (op == "!") throw new FormulaException("연산자 '!'는 지원하지 않습니다('!=' 형태만 허용).");
                tokens.Add(new("op", op == "=" ? "==" : op));
                i += two ? 2 : 1;
                continue;
            }
            if (char.IsDigit(c) || (c == '-' && i + 1 < input.Length && char.IsDigit(input[i + 1])))
            {
                var start = i; i++;
                while (i < input.Length && (char.IsDigit(input[i]) || input[i] == '.')) i++;
                tokens.Add(new("num", input[start..i]));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] is '_' or '-' or '.')) i++;
                var word = input[start..i];
                if (word.Equals("AND", StringComparison.OrdinalIgnoreCase)) tokens.Add(new("and", word));
                else if (word.Equals("OR", StringComparison.OrdinalIgnoreCase)) tokens.Add(new("or", word));
                else tokens.Add(new("id", word));
                continue;
            }
            throw new FormulaException($"해석할 수 없는 문자 '{c}' (위치 {i}).");
        }
        return tokens;
    }

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private readonly IReadOnlyDictionary<string, decimal> _values;
        private int _pos;

        public Parser(List<Token> tokens, IReadOnlyDictionary<string, decimal> values)
        {
            _tokens = tokens;
            _values = values;
        }

        public bool ParseExpression()   // OR — 최저 우선순위
        {
            var left = ParseTerm();
            while (Peek()?.Kind == "or") { _pos++; var right = ParseTerm(); left = left || right; }
            return left;
        }

        private bool ParseTerm()        // AND
        {
            var left = ParseFactor();
            while (Peek()?.Kind == "and") { _pos++; var right = ParseFactor(); left = left && right; }
            return left;
        }

        private bool ParseFactor()
        {
            if (Peek()?.Kind == "lparen")
            {
                _pos++;
                var inner = ParseExpression();
                if (Peek()?.Kind != "rparen") throw new FormulaException("닫는 괄호 ')'가 없습니다.");
                _pos++;
                return inner;
            }
            return ParseComparison();
        }

        private bool ParseComparison()
        {
            var left = ParseOperand();
            var op = Peek();
            if (op is not { Kind: "op" })
                throw new FormulaException("비교 연산자(>, >=, <, <=, ==, !=)가 필요합니다.");
            _pos++;
            var right = ParseOperand();
            return op.Value.Text switch
            {
                ">" => left > right,
                ">=" => left >= right,
                "<" => left < right,
                "<=" => left <= right,
                "==" => left == right,
                "!=" => left != right,
                _ => throw new FormulaException($"지원하지 않는 연산자 '{op.Value.Text}'."),
            };
        }

        private decimal ParseOperand()
        {
            var t = Peek() ?? throw new FormulaException("피연산자가 필요합니다(수식이 중간에 끝남).");
            _pos++;
            if (t.Kind == "num")
                return decimal.Parse(t.Text, System.Globalization.CultureInfo.InvariantCulture);
            if (t.Kind == "id")
            {
                foreach (var (key, value) in _values)
                    if (string.Equals(key, t.Text, StringComparison.OrdinalIgnoreCase)) return value;
                throw new FormulaException($"파라미터 '{t.Text}'의 최신 수집 값이 없습니다.");
            }
            throw new FormulaException($"피연산자 자리에 '{t.Text}'가 왔습니다.");
        }

        public void ExpectEnd()
        {
            if (_pos < _tokens.Count)
                throw new FormulaException($"수식 끝에 해석되지 않은 토큰 '{_tokens[_pos].Text}'가 남았습니다.");
        }

        private Token? Peek() => _pos < _tokens.Count ? _tokens[_pos] : null;
    }
}
