﻿module ParserCombinators
open Utils

// syntax:
// let rec nameOfRule() = () |> buildRule()       <<-- the `() |> ` is for lazy eval. manipulation
// and otherRule = () |> buildRule(nameOfRule)    <<-- don't pass `()` to nameOfRule

type ParseResult<'input, 'output> = Result<'input * 'output, string Lazy>

// use the below for cleaner recursive parsers
// type Rule<'input, 'output_inner, 'output> =
//   |Match of recognizer: ('input -> 'output ParseResult)
//   |Map of ('output_inner -> 'output)
//   |Series of ('input -> 'output_inner ParseResult) * ('input -> 'output ParseResult)  // returns ('output_inner, 'output)
//   |Parallel of ('input -> 'output ParseResult) * ('input -> 'output ParseResult)
  
type Rule<'input, 'output> = 'input -> ParseResult<'input, 'output>

let ParseResultMap (binding: 'o1 -> 'o2) = Result.map (fun (i, o) -> (i, binding o))
  
let Map (binding: 'o1 -> 'o2) (rule: Rule<'i, 'o1>) : Rule<'i, 'o2> =
  rule >> ParseResultMap binding
let (<-/) = Map
let (->/) r f = Map f r

let Series (rule1: Rule<'i, 'o1>) (rule2: Rule<'i, 'o2>) : Rule<'i, 'o1 * 'o2> = fun input ->
  rule1 input
   |> Result.bind (fun (input', res1) -> ParseResultMap (mkpair res1) (rule2 input'))
let (+/) = Series
type SequenceBuilder() =
  member __.Bind(rule: Rule<'i, 'o1>, cont: 'o1 -> Rule<'i, 'o2>) : Rule<'i, 'o2> = fun input ->
    match rule input with
    |Ok(input', x) -> cont x input'
    |Error msg -> Error msg
  member __.Return(x: 'o) : Rule<'i, 'o> = fun input -> Ok(input, x)
  // member __.Zero() : Rule<'i, unit> = fun input -> Ok(input, ())
let SequenceOf = SequenceBuilder()

let combine_errors _ = id
let Parallel (rule1: Rule<'i, 'o>) (rule2: Rule<'i, 'o>) : Rule<'i, 'o> = fun input ->
  match rule1 input with
  |Ok res -> Ok res
  |Error err -> combine_errors err (rule2 input)
let (|/) = Parallel
let OneOf (rules: Rule<'i, 'o> list) : Rule<'i, 'o> = List.reduce (|/) rules

let Optional (rule: Rule<'i, 'o>) : Rule<'i, 'o option> = fun input ->
  match rule input with
  |Ok(input', res) -> Ok(input', Some res)
  |Error _ -> Ok(input, None)

let FoldListOf (f: 'o -> Rule<'i, 'o>) (z: 'o) : Rule<'i, 'o> =
  let rec parse_list acc input =
    match f acc input with
    |Ok(input', res) -> parse_list res input'
    |Error _ -> Ok(input, acc)
  parse_list z

let FoldBackListOf (f: 'o1 -> 'o -> 'o) (rule: Rule<'i, 'o1>) (z: Rule<'i, 'o>) : Rule<'i, 'o> =
  let rec parse_list input =
    match rule input with
    |Ok(input', res) -> ParseResultMap (f res) (parse_list input')
    |Error _ -> z input
  parse_list

let OptionalListOf rule : Rule<'i, 'o list> = FoldBackListOf (fun a b -> a::b) rule (fun i -> Ok(i, []))

let ListOf rule : Rule<'i, 'o list> = rule +/ OptionalListOf rule ->/ List.Cons

let JoinedListOf (rule: Rule<'i, 'o>) (sep: Rule<'i, unit>) : Rule<'i, 'o list> =
  rule +/ (OptionalListOf (sep +/ rule ->/ snd)) ->/ List.Cons

let LookAhead (rule: Rule<'i, 'o>) : Rule<'i, 'o> = fun input ->
  Result.map (fun (i, o) -> (input, o)) (rule input)
let (&/) rule1 rule2 = LookAhead rule1 +/ rule2 ->/ snd

let inline Equal (token: string) : Rule< ^I, 'o> = fun input ->
  match (^I: (member atomic_equal: (string -> Option< ^I * 'o>)) input) token with
  |Some(rest, result) -> Ok(rest, result)
  |None -> Error (lazy sprintf "expected token %A" token)
let inline (~%) x = Equal x
let inline (!) x = Equal x ->/ ignore

let inline Match (rgx: string) : Rule< ^I, 'o> = fun input ->
  match (^I: (member atomic_match: (string -> Option< ^I * 'o>)) input) rgx with
  |Some(rest, result) -> Ok(rest, result)
  |None -> Error (lazy sprintf "expected regex %A" rgx)
let inline (~%%) rgx = Match rgx
let inline (!!) rgx = Match rgx ->/ ignore

let inline End (input: ^I): ParseResult< ^I, unit> =
  let empty = (^I: (static member empty: ^I) ())
  if input = empty then Ok(empty, ()) else Error (lazy "expected end of stream")

let inline run_parser (parse_rule: Rule< ^I, 'o>) (input: ^I) : 'o =
  match parse_rule input with
  |Ok(rest, result) ->
    if rest = (^I: (static member empty: ^I) ())
     then result
     else failwithf "parser did not reach end: result = %A, remaining = %A" result rest
  |Error msg -> failwith (msg.Force())
