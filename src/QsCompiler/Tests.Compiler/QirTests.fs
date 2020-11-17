﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

module Microsoft.Quantum.QsCompiler.Testing.QirTests

open System
open System.IO
open Microsoft.Quantum.QsCompiler
open Microsoft.Quantum.QsCompiler.CommandLineCompiler
open Microsoft.Quantum.QsCompiler.CompilationBuilder
open Xunit

let private pathRoot = 
    Path.GetPathRoot(Directory.GetCurrentDirectory())

let private parentDir = 
    Path.GetDirectoryName(Directory.GetCurrentDirectory())

let private testOne expected args = 
    let result = Program.Main args
    Assert.Equal(expected, result)

let private testInput expected args =
    [
        [|"diagnose"|]
        [|"build"; "-o"; "outputFolder"|]
    ] 
    |> List.iter (fun v -> Array.append v args |> testOne expected) 

let private testSnippet expected args = 
    [
        [|"diagnose"|]
        [|"build"|]
    ] 
    |> List.iter (fun v -> Array.append v args |> testOne expected) 

let private clearOutput name =
    File.WriteAllText(name, "Test did not run to completion")

let private checkOutput name =
    let expectedText = ("TestCases","QirTests",name) |> Path.Combine |> File.ReadAllText
    let actualText = name |> File.ReadAllText
    Assert.Contains(expectedText, actualText)

let private checkAltOutput name actualText =
    let expectedText = ("TestCases","QirTests",name) |> Path.Combine |> File.ReadAllText
    Assert.Contains(expectedText, actualText)

let private qirTest target name =
    clearOutput (name+".ll")
    [|
        "build"
        "-o"
        "outputFolder"
        "--proj"
        name
        "--input"
        ("TestCases","QirTests",name+".qs") |> Path.Combine
        ("TestCases","QirTests","QirCore.qs") |> Path.Combine
        (if target then ("TestCases","QirTests","QirTarget.qs") |> Path.Combine else "")
        "--qir"
        "--verbosity" 
        "Diagnostic"
    |]
    |> testOne ReturnCode.SUCCESS
    checkOutput (name+".ll")

let private qirExeTest target name =
    [|
        "build"
        "-o"
        "outputFolder"
        "--proj"
        name
        "--build-exe"
        "--input"
        ("TestCases","QirTests",name+".qs") |> Path.Combine
        ("TestCases","QirTests","QirCore.qs") |> Path.Combine
        (if target then ("TestCases","QirTests","QirTarget.qs") |> Path.Combine else "")
        "--qir"
        "--verbosity" 
        "Diagnostic"
    |]
    |> testOne ReturnCode.SUCCESS
    checkOutput (name+".ll")

let private qirExeMultiTest target name snippets =
    [|
        "build"
        "-o"
        "outputFolder"
        "--proj"
        name
        "--build-exe"
        "--input"
        ("TestCases","QirTests",name+".qs") |> Path.Combine
        ("TestCases","QirTests","QirCore.qs") |> Path.Combine
        (if target then ("TestCases","QirTests","QirTarget.qs") |> Path.Combine else "")
        "--qir"
        "--verbosity" 
        "Diagnostic"
    |]
    |> testOne ReturnCode.SUCCESS
    let actualText = (name+".ll") |> File.ReadAllText
    snippets |> List.map (fun s -> checkAltOutput (s+".ll") actualText)

[<Fact>]
let ``QIR using`` () =
    qirTest false "TestUsing"
    
[<Fact>]
let ``QIR array loop`` () =
    qirTest false "TestArrayLoop"
    
[<Fact>]
let ``QIR array update`` () =
    qirTest false "TestArrayUpdate"
    
[<Fact>]
let ``QIR tuple deconstructing`` () =
    qirTest false "TestDeconstruct"
    
[<Fact>]
let ``QIR UDT constructor`` () =
    qirTest false "TestUdt"
    
[<Fact>]
let ``QIR UDT construction`` () =
    qirTest false "TestUdtConstruction"
    
[<Fact>]
let ``QIR UDT accessor`` () =
    qirTest false "TestUdtAccessor"
    
[<Fact>]
let ``QIR UDT update`` () =
    qirTest false "TestUdtUpdate"
    
[<Fact>]
let ``QIR while loop`` () =
    qirTest false "TestWhile"
    
[<Fact>]
let ``QIR repeat loop`` () =
    qirTest true "TestRepeat"
    
[<Fact>]
let ``QIR integers`` () =
    qirTest false "TestIntegers"

[<Fact>]
let ``QIR doubles`` () =
    qirTest false "TestDoubles"

[<Fact>]
let ``QIR bools`` () =
    qirTest false "TestBools"

[<Fact>]
let ``QIR bigints`` () =
    qirTest false "TestBigInts"

[<Fact>]
let ``QIR controlled partial applications`` () =
    qirExeMultiTest true "TestControlled" ["TestControlled1"; "TestControlled2"]

[<Fact>]
let ``QIR entry points`` () =
    qirExeTest false "TestEntryPoint"

[<Fact>]
let ``QIR partial applications`` () =
    qirExeMultiTest true "TestPartials" ["TestPartials1"; "TestPartials2"; "TestPartials3"]

[<Fact>]
let ``QIR paulis`` () =
    qirTest false "TestPaulis"

[<Fact>]
let ``QIR results`` () =
    qirTest false "TestResults"

[<Fact>]
let ``QIR ranges`` () =
    qirTest false "TestRange"

[<Fact>]
let ``QIR strings`` () =
    qirTest false "TestStrings"

[<Fact>]
let ``QIR scoping`` () =
    qirTest false "TestScoping"
