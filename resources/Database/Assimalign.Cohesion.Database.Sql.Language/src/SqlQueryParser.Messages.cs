namespace Assimalign.Cohesion.Database.Sql.Language;

public sealed partial class SqlQueryParser
{
    // Expected-token context is also evaluated on successful parses. Cache resource templates
    // once, keeping those paths free of repeated ResourceManager lookups.
    private static readonly string _errorArrayTypeClosingExpected = ErrorMessages.ArrayTypeClosingExpected;
    private static readonly string _errorCastTargetSubject = ErrorMessages.CastTargetSubject;
    private static readonly string _errorColumnSubject = ErrorMessages.ColumnSubject;
    private static readonly string _errorCteBodyClosingExpected = ErrorMessages.CteBodyClosingExpected;
    private static readonly string _errorCteBodyOpeningExpected = ErrorMessages.CteBodyOpeningExpected;
    private static readonly string _errorDomainCheckOpeningExpected = ErrorMessages.DomainCheckOpeningExpected;
    private static readonly string _errorDomainSubject = ErrorMessages.DomainSubject;
    private static readonly string _errorEndOfStatement = ErrorMessages.EndOfStatement;
    private static readonly string _errorExistsClosingExpected = ErrorMessages.ExistsClosingExpected;
    private static readonly string _errorExistsOpeningExpected = ErrorMessages.ExistsOpeningExpected;
    private static readonly string _errorFunctionArgumentsClosingExpected = ErrorMessages.FunctionArgumentsClosingExpected;
    private static readonly string _errorFunctionClosingParenthesisExpected = ErrorMessages.FunctionClosingParenthesisExpected;
    private static readonly string _errorFunctionParametersOpeningExpected = ErrorMessages.FunctionParametersOpeningExpected;
    private static readonly string _errorFunctionParameterSubject = ErrorMessages.FunctionParameterSubject;
    private static readonly string _errorFunctionParameterTypeSubject = ErrorMessages.FunctionParameterTypeSubject;
    private static readonly string _errorFunctionReturnTypeSubject = ErrorMessages.FunctionReturnTypeSubject;
    private static readonly string _errorFunctionSignatureOpeningExpected = ErrorMessages.FunctionSignatureOpeningExpected;
    private static readonly string _errorIdentityOptionsClosingExpected = ErrorMessages.IdentityOptionsClosingExpected;
    private static readonly string _errorImplicitTableAliasHint = ErrorMessages.ImplicitTableAliasHint;
    private static readonly string _errorInListClosingParenthesisExpected = ErrorMessages.InListClosingParenthesisExpected;
    private static readonly string _errorInSubqueryClosingParenthesisExpected = ErrorMessages.InSubqueryClosingParenthesisExpected;
    private static readonly string _errorQueryOperandClosingExpected = ErrorMessages.QueryOperandClosingExpected;
    private static readonly string _errorStringLiteralTokenDescription = ErrorMessages.StringLiteralTokenDescription;
    private static readonly string _errorSubqueryClosingParenthesisExpected = ErrorMessages.SubqueryClosingParenthesisExpected;
    private static readonly string _errorValuesRowClosingExpected = ErrorMessages.ValuesRowClosingExpected;
    private static readonly string _errorValuesRowOpeningExpected = ErrorMessages.ValuesRowOpeningExpected;
    private static readonly string _errorViewColumnsClosingExpected = ErrorMessages.ViewColumnsClosingExpected;
    private static readonly string _errorWithinGroupClosingExpected = ErrorMessages.WithinGroupClosingExpected;
    private static readonly string _errorWithinGroupOpeningExpected = ErrorMessages.WithinGroupOpeningExpected;
}
