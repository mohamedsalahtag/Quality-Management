using System.Globalization;
using System.Text;

namespace SharbatlyQMS.Web.Services.Reports;

/// <summary>
/// A deliberately tiny, safe arithmetic evaluator for Report Builder calculated
/// columns. It understands only:
///   * number literals            (12, 3.5)
///   * the operators + - * / and parentheses, with unary minus
///   * column references in square brackets, e.g. [Sample Size]
///
/// Anything else is a validation error. There is no SQL, no reflection, no code
/// execution — a formula can only add/subtract/multiply/divide numbers that the
/// exporter has already computed for the row. A reference to a null column, or a
/// division by zero, yields a null result (an empty Excel cell) rather than an
/// exception, so one bad row never aborts an export.
/// </summary>
public static class ReportFormula
{
    public sealed record ValidationResult(bool Ok, string? Error);

    /// <summary>Parses the formula and checks every [Column] reference exists in
    /// <paramref name="availableColumns"/>. Does not need row data.</summary>
    public static ValidationResult Validate(string? formula, IEnumerable<string> availableColumns)
    {
        if (string.IsNullOrWhiteSpace(formula))
            return new(false, "Formula is empty.");
        var cols = new HashSet<string>(availableColumns, StringComparer.OrdinalIgnoreCase);
        try
        {
            var tokens = Tokenize(formula);
            foreach (var t in tokens)
                if (t.Kind == TokKind.Column && !cols.Contains(t.Text))
                    return new(false, $"Unknown column: [{t.Text}]");
            var rpn = ToRpn(tokens);                 // throws FormulaException on bad structure
            _ = EvalRpn(rpn, _ => 0d);               // structural dry-run (all columns = 0)
            return new(true, null);
        }
        catch (FormulaException ex) { return new(false, ex.Message); }
    }

    /// <summary>Evaluates the formula over the row's numeric column values (keyed
    /// by column label, case-insensitive). Returns null when a referenced column
    /// is null or a division by zero occurs.</summary>
    public static double? Evaluate(string? formula, IReadOnlyDictionary<string, double?> values)
    {
        if (string.IsNullOrWhiteSpace(formula)) return null;
        try
        {
            var rpn = ToRpn(Tokenize(formula));
            return EvalRpn(rpn, name =>
            {
                values.TryGetValue(name, out var v);
                return v;   // null propagates
            });
        }
        catch (FormulaException) { return null; }
    }

    // ---- tokenizer ---------------------------------------------------------
    private enum TokKind { Number, Column, Op, LParen, RParen }
    private readonly record struct Tok(TokKind Kind, string Text, double Num = 0);

    private static List<Tok> Tokenize(string s)
    {
        var toks = new List<Tok>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '(') { toks.Add(new(TokKind.LParen, "(")); i++; continue; }
            if (c == ')') { toks.Add(new(TokKind.RParen, ")")); i++; continue; }
            if (c is '+' or '-' or '*' or '/') { toks.Add(new(TokKind.Op, c.ToString())); i++; continue; }
            if (c == '[')
            {
                int end = s.IndexOf(']', i + 1);
                if (end < 0) throw new FormulaException("Unclosed [ in column reference.");
                var name = s.Substring(i + 1, end - i - 1).Trim();
                if (name.Length == 0) throw new FormulaException("Empty column reference [].");
                toks.Add(new(TokKind.Column, name));
                i = end + 1;
                continue;
            }
            if (char.IsDigit(c) || c == '.')
            {
                int start = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                var text = s.Substring(start, i - start);
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                    throw new FormulaException($"Bad number '{text}'.");
                toks.Add(new(TokKind.Number, text, num));
                continue;
            }
            throw new FormulaException($"Unexpected character '{c}'.");
        }
        return toks;
    }

    // ---- shunting-yard to RPN, with unary minus ----------------------------
    private const string UnaryMinus = "u-";

    private static int Prec(string op) => op switch
    {
        UnaryMinus => 3,
        "*" or "/" => 2,
        "+" or "-" => 1,
        _ => 0
    };

    private static List<Tok> ToRpn(List<Tok> toks)
    {
        var output = new List<Tok>();
        var ops = new Stack<Tok>();
        TokKind? prev = null;

        foreach (var raw in toks)
        {
            var t = raw;
            switch (t.Kind)
            {
                case TokKind.Number:
                case TokKind.Column:
                    output.Add(t);
                    break;

                case TokKind.Op:
                    // A '-' at the start, after another operator, or after '(' is unary.
                    bool unary = t.Text == "-" &&
                                 (prev is null or TokKind.Op or TokKind.LParen);
                    var op = unary ? new Tok(TokKind.Op, UnaryMinus) : t;
                    bool rightAssoc = op.Text == UnaryMinus;
                    while (ops.Count > 0 && ops.Peek().Kind == TokKind.Op &&
                           (rightAssoc ? Prec(ops.Peek().Text) > Prec(op.Text)
                                       : Prec(ops.Peek().Text) >= Prec(op.Text)))
                        output.Add(ops.Pop());
                    ops.Push(op);
                    break;

                case TokKind.LParen:
                    ops.Push(t);
                    break;

                case TokKind.RParen:
                    while (ops.Count > 0 && ops.Peek().Kind != TokKind.LParen)
                        output.Add(ops.Pop());
                    if (ops.Count == 0) throw new FormulaException("Unbalanced parentheses.");
                    ops.Pop(); // discard the '('
                    break;
            }
            prev = t.Kind;
        }
        while (ops.Count > 0)
        {
            var op = ops.Pop();
            if (op.Kind is TokKind.LParen or TokKind.RParen)
                throw new FormulaException("Unbalanced parentheses.");
            output.Add(op);
        }
        return output;
    }

    private static double? EvalRpn(List<Tok> rpn, Func<string, double?> resolve)
    {
        var st = new Stack<double?>();
        foreach (var t in rpn)
        {
            switch (t.Kind)
            {
                case TokKind.Number: st.Push(t.Num); break;
                case TokKind.Column: st.Push(resolve(t.Text)); break;
                case TokKind.Op when t.Text == UnaryMinus:
                    if (st.Count < 1) throw new FormulaException("Missing operand.");
                    var u = st.Pop();
                    st.Push(u is null ? (double?)null : -u.Value);
                    break;
                case TokKind.Op:
                    if (st.Count < 2) throw new FormulaException("Missing operand.");
                    var b = st.Pop(); var a = st.Pop();
                    st.Push(Apply(t.Text, a, b));
                    break;
                default:
                    throw new FormulaException("Malformed formula.");
            }
        }
        if (st.Count != 1) throw new FormulaException("Malformed formula.");
        return st.Pop();
    }

    private static double? Apply(string op, double? a, double? b)
    {
        if (a is null || b is null) return null;   // null propagation
        return op switch
        {
            "+" => a + b,
            "-" => a - b,
            "*" => a * b,
            "/" => b.Value == 0 ? (double?)null : a / b,
            _ => throw new FormulaException($"Bad operator '{op}'.")
        };
    }

    private sealed class FormulaException : Exception
    {
        public FormulaException(string message) : base(message) { }
    }
}
