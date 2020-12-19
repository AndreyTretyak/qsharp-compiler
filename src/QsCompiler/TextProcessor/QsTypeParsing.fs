﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

module Microsoft.Quantum.QsCompiler.TextProcessing.TypeParsing

open FParsec
open Microsoft.Quantum.QsCompiler.DataTypes
open Microsoft.Quantum.QsCompiler.Diagnostics
open Microsoft.Quantum.QsCompiler.SyntaxTokens
open Microsoft.Quantum.QsCompiler.TextProcessing.Keywords
open Microsoft.Quantum.QsCompiler.TextProcessing.ParsingPrimitives
open Microsoft.Quantum.QsCompiler.TextProcessing.SyntaxBuilder
open Microsoft.Quantum.QsCompiler.TextProcessing.SyntaxExtensions


// processing transformation characteristics

/// returns a characteristics expression representing invalid characteristics
let private invalidCharacteristics = (InvalidSetExpr, Null) |> Characteristics.New

/// operator precedence parser for characteristics expressions
let private characteristicsExpression = new OperatorPrecedenceParser<Characteristics, _, _>()

/// For a characteristics expression of the given kind that is built from a left and right hand expression with the given ranges,
/// builds the corresponding expression with its range set to the combined range.
/// If either one of given ranges is Null, builds an invalid expression with its range set to Null.
let private buildCombinedExpression kind (lRange, rRange) =
    // *needs* to be invalid if the combined range is Null!
    match QsNullable.Map2 Range.Span lRange rRange with
    | Value range -> { Characteristics = kind; Range = Value range }
    | Null -> { Characteristics = InvalidSetExpr; Range = Null }

let private applyBinary operator _ (left: Characteristics) (right: Characteristics) =
    buildCombinedExpression (operator (left, right)) (left.Range, right.Range)

characteristicsExpression.AddOperator
    (InfixOperator(qsSetUnion.op, emptySpace, qsSetUnion.prec, qsSetUnion.Associativity, (), applyBinary Union))

characteristicsExpression.AddOperator
    (InfixOperator
        (qsSetIntersection.op,
         emptySpace,
         qsSetIntersection.prec,
         qsSetIntersection.Associativity,
         (),
         applyBinary Intersection))

/// Parses for an arbitrary characteristics expression.
/// Fails on all reserved keywords except the ones denoting predefined sets of operation characteristics.
/// Raises an UnknownSetName error for any word-like expression that is not a know set, returning an invalid expression.
let private characteristics = characteristicsExpression.ExpressionParser

/// Given a continuation (parser), attempts to parse an arbitrary characteristics expression,
/// returning the parsed expression, or an expression representing an invalid characteristics if the parsing fails.
/// On failure, either raises a MissingOperationCharacteristics error if the given continuation succeeds at the current position,
/// or raises an InvalidOperationCharacteristics error and advances until the given continuation succeeds otherwise.
/// Does not apply the given continuation.
let internal expectedCharacteristics continuation =
    expected
        characteristics
        ErrorCode.InvalidOperationCharacteristics
        ErrorCode.MissingOperationCharacteristics
        invalidCharacteristics
        continuation

let private buildCharacteristics t (range: Range) = (t, range) |> Characteristics.New

characteristicsExpression.TermParser <-
    let unknownSet =
        let identifier =
            IdentifierOptions(isAsciiIdStart = isSymbolContinuation, isAsciiIdContinue = isSymbolContinuation)
            |> identifier

        let anyWord = buildError (term identifier |>> snd) ErrorCode.UnknownSetName
        notFollowedBy qsReservedKeyword >>. anyWord // check for reserved keyword is needed here!

    let tupledSetExpr = tupleBrackets (expectedCharacteristics eof |> withExcessContinuation eof)

    choice [ tupledSetExpr |>> fst
             qsCtlSet.parse |>> buildCharacteristics (SimpleSet Controllable)
             qsAdjSet.parse |>> buildCharacteristics (SimpleSet Adjointable)
             unknownSet >>% invalidCharacteristics ] // needs to be at the end!


// simple types and utils

/// returns a Q# type representing an invalid type (i.e. syntax error on parsing)
let internal invalidType = (InvalidType, Null) |> QsType.New

let private asType kind (t, range: Range) = (kind t, range) |> QsType.New

let (internal qsType, private qsTypeImpl) = createParserForwardedToRef ()

/// Given a continuation (parser), attempts to parse a Q# type,
/// returning the parsed Q# type, or a Q# type representing an invalid type (parsing failure) if the parsing fails.
/// On failure, either raises an MissingTypeDeclaration if the given continuation succeeds at the current position,
/// or raises an InvalidTypeDeclaration and advanced until the given continuation succeeds otherwise.
/// Does not apply the given continuation.
let internal expectedQsType continuation =
    expected qsType ErrorCode.InvalidType ErrorCode.MissingType invalidType continuation

/// returns a parser for the Q# Unit type that raises a warning upon using deprecated syntax
let private unitType =
    let deprecated = buildWarning (tupleBrackets emptySpace |>> snd) WarningCode.DeprecatedUnitType
    (qsUnit.parse <|> deprecated) |>> fun range -> (UnitType, range) |> QsType.New

/// Parses a Q# atomic type - i.e. non-array, non-tuple, and not function or operation types.
/// NOTE: does *not* parse Unit, since Unit must be parsed before trying to parse a tuple type, but after operation and function types.
/// Does also *not* parse user defined types.
let private atomicType =
    let buildType t (range: Range) = (t, range) |> QsType.New

    choice [ qsInt.parse |>> buildType Int
             qsBigInt.parse |>> buildType BigInt
             qsDouble.parse |>> buildType Double
             qsBool.parse |>> buildType Bool
             qsQubit.parse |>> buildType Qubit
             qsResult.parse |>> buildType Result
             qsPauli.parse |>> buildType Pauli
             qsRange.parse |>> buildType Range
             qsString.parse |>> buildType String ]

/// Parses a Q# user defined type (possibly qualified symbol), raising an InvalidTypeName error if needed.
/// Note: As long as the parser succeeds, the returned Q# type is of kind UserDefinedType even if the parsed qualified symbol is invalid.
let private userDefinedType =
    multiSegmentSymbol ErrorCode.InvalidTypeName
    |>> asQualifiedSymbol
    |>> function
    | { Symbol = InvalidSymbol } -> (InvalidType, Null) |> QsType.New
    | symbol -> (UserDefinedType symbol, symbol.Range) |> QsType.New


// composite types

/// <summary>
/// Parses a Q# operation type.
/// </summary>
/// <remarks>
/// Inner parenthesis of the form "((T1 => T2) is Adj)" are optional. Uses leftRecursionByInfix to process the signature
/// and raise suitable errors.
/// </remarks>
let private operationType =
    // utils for handling deprecated and partially deprecated syntax:
    let quantumFunctor =
        choice [ (qsControlledFunctor.parse |>> buildCharacteristics (SimpleSet Controllable))
                 (qsAdjointFunctor.parse |>> buildCharacteristics (SimpleSet Adjointable))
                 (qsCtlSet.parse |>> buildCharacteristics (SimpleSet Controllable))
                 (qsAdjSet.parse |>> buildCharacteristics (SimpleSet Adjointable)) ]

    let functorSupport startPos =
        sepBy1 quantumFunctor (comma >>? followedBy quantumFunctor)
        >>= function // fail on comma followed by something else than a functor
        | head :: tail ->
            let setExpr =
                tail |> List.fold (fun acc x -> buildCombinedExpression (Union(acc, x)) (acc.Range, x.Range)) head

            match setExpr.Range with
            | Null -> preturn setExpr
            | Value range ->
                let characteristics =
                    head :: tail
                    |> List.choose (fun a ->
                        match a.Characteristics with
                        | SimpleSet Controllable -> Some qsCtlSet.id
                        | SimpleSet Adjointable -> Some qsAdjSet.id
                        | _ -> None)
                    |> String.concat qsSetUnion.op
                    |> sprintf "%s %s" qsCharacteristics.id

                QsCompilerDiagnostic.Warning
                    (WarningCode.DeprecatedOpCharacteristics, [ characteristics ])
                    (Range.Create startPos range.End)
                |> pushDiagnostic
                >>. preturn setExpr
        | _ -> fail "not a functor support annotation"

    // the actual type parsing:
    let inAndOutputType =
        let continuation = isTupleContinuation <|> followedBy qsCharacteristics.parse <|> followedBy colon
        leftRecursionByInfix opArrow qsType (expectedQsType continuation)

    let opTypeWith characteristics = inAndOutputType .>>. characteristics
    let opTypeWithoutCharacteristics = inAndOutputType .>>. preturn (Characteristics.New(EmptySet, Null))

    let deprecatedCharacteristics =
        let colonWithWarning = buildWarning (getEmptyRange .>> colon) WarningCode.DeprecatedOpCharacteristicsIntro

        attempt (colonWithWarning >>. characteristics .>> notFollowedBy (comma >>. quantumFunctor))
        <|> (qsCharacteristics.parse |>> (fun r -> r.Start) <|> (getPosition .>> colon) >>= functorSupport)

    let characteristics =
        qsCharacteristics.parse >>. expectedCharacteristics isTupleContinuation
        .>> notFollowedBy (comma >>. quantumFunctor)

    // keep this order!
    attempt (opTypeWith characteristics)
    <|> attempt (opTypeWith deprecatedCharacteristics)
    <|> opTypeWithoutCharacteristics
    |> term
    |>> asType Operation

/// <summary>
/// Parses a Q# function type.
/// </summary>
/// <remarks>
/// Uses leftRecursionByInfix to process the signature and raise suitable errors.
/// </remarks>
let private functionType =
    leftRecursionByInfix fctArrow qsType (expectedQsType isTupleContinuation) |> term
    |>> asType Function

/// Parses a Q# tuple type, raising an Missing- or InvalidTypeDeclaration error for missing or invalid items.
/// The tuple must consist of at least one tuple item.
let internal tupleType =
    let buildTupleType (items, range: Range) = (TupleType items, range) |> QsType.New
    buildTuple qsType buildTupleType ErrorCode.InvalidType ErrorCode.MissingType invalidType

/// Parses for an arbitrary Q# type, using the given parser to process tuple types.
let internal typeParser tupleType =
    let baseType =
        [
            // operation and function signatures need to be processed *first* to make the left recursion work!
            operationType
            functionType

            unitType // needs to come *before* tupleType but *after* function- and operationType ...
            tupleType
            typeParameterLike
            atomicType
            userDefinedType // needs to be last
        ]
        |> choice

    let validate { Type = kind; Range = range } isArrayItem =
        let start = (range.ValueOr Range.Zero).Start
        let end' = (range.ValueOr Range.Zero).End

        match kind with
        | Operation _ when isArrayItem ->
            // To avoid confusing syntax like "Qubit => Unit is Adj[]", require operation types to be tupled when used
            // as an array item type.
            [
                QsCompilerDiagnostic.Error (ErrorCode.MissingLTupleBracket, []) (Range.Create start start)
                QsCompilerDiagnostic.Error (ErrorCode.MissingRTupleBracket, []) (Range.Create end' end')
            ]
            |> pushDiagnostics
        | _ -> preturn ()

    let combine kind (lRange, rRange) =
        match QsNullable.Map2 Range.Span lRange rRange with
        | Value range -> { Type = kind; Range = Value range }
        | Null -> { Type = InvalidType; Range = Null } // *needs* to be invalid if the combined range is Null!

    let createArray itemType range =
        combine (ArrayType itemType) (itemType.Range, Value range)

    baseType .>>. many (arrayBrackets emptySpace)
    >>= fun (itemType, brackets) ->
            validate itemType (List.isEmpty brackets |> not)
            >>. preturn (brackets |> Seq.map snd |> Seq.fold createArray itemType)

do qsTypeImpl := typeParser tupleType
