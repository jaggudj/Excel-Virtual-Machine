﻿namespace Parser
open Lexer
open Token

module FSharp =
  let preprocess =
    List.fold (fun ((r,c),acc) (e:string) ->
      if e.[0] = '\n' then        (r+1,e.Replace("\n", "").Length),acc
      else if ruleset " " e then  (r,c+e.Length),acc
      else if e = "." then        (r,c+e.Length),Token(e, (r,c), false, [])::acc
      else                        (r,c+e.Length),Token(e, (r,c))::acc
     ) ((1,1),[])
     >> snd >> List.rev
     >> List.fold (fun (earliestInRow, acc) e ->
          if e.Name = "fun" && fst e.Indentation = fst earliestInRow then
            (earliestInRow, Token("fun", earliestInRow)::acc)
          elif fst e.Indentation > fst earliestInRow then
            (e.Indentation, e::acc)
          else
            (earliestInRow, e::acc)
         ) ((-1, 0), [])
     >> snd >> List.rev

  let (|ConsqLR|_|) = function
    |(T _ as a)::_, (T _ as b)::_ when a.IndentedLess b -> Some ConsqLR
    |_ -> None
  let (|ConsqS|_|) = function
    |(T _ as a)::(T _ as b)::_ when a.IndentedLess b -> Some ConsqS
    |_ -> None

  type State =
    |Pattern
    |Normal
  let rec parse state (stop:Token list->bool) (fail:Token list->bool) left right =
    //printfn "%A" (left, right)
    match state with
    |Normal ->
      match (left:Token list), (right:Token list) with
      |t::_, _ when stop right -> Token("sequence", t.Indentation, List.rev left), right
      |_ when stop right -> Token("()", (0,0), left), right
      |_ when fail right -> failwithf "unexpected end of context %A" right
      |Brackets state stop fail x
      |If state stop fail x
      |Do state stop fail x
      |While state stop fail x
      |For state stop fail x
      |Let state stop fail x
      |Fun state stop fail x
      |PatternCase state stop fail x
      |Match state stop fail x
      |Function state stop fail x
      |Dot state stop fail x
      |Infix state stop fail x
      |Tuple state stop fail x           //more testing needed
      |Apply state stop fail x
      |Transfer state stop fail x             -> x
      |_, [] -> Token("()", (0,0)), []
    |Pattern ->
      match (left:Token list), (right:Token list) with
      |t::_, _ when stop right -> Token("sequence", t.Indentation, List.rev left), right
      |_ when stop right -> Token("()", (0,0), left), right
      |_ when fail right -> failwithf "unexpected end of context %A" right
      |PatternBrackets state stop fail x
      |PatternComb state stop fail x
      |Tuple state stop fail x
      |Apply state stop fail x
      |Transfer state stop fail x         -> x
      |_, [] -> Token("()", (0,0)), []
  and (|Brackets|_|) state stop fail = function
    |left:Token list, (T "(" as t)::restr ->
      let indent =
        match left with
        |p::_ when fst p.Indentation = fst t.Indentation -> p.Indentation
        |_ -> t.Indentation
      let parsed, T ")"::restr =
        match restr with
        |T ")"::_ -> Token("()", t.Indentation, true, []), restr
        |_ ->
          parse state (function T ")"::_ -> true | t'::_ when snd indent > snd t'.Indentation -> true | _ -> false)
           (fun _ -> false)    //intermediate step keywords should fail
           [] restr
      let restr = Token("()", t.Indentation, true, [parsed])::restr
      Some (parse state stop fail left restr)
    |_ -> None
  and (|If|_|) state stop fail = function
    |T "if" as t::restl, right ->
      let cond, T "then"::restr =
        parse state (function T "then"::_ -> true | _ -> false)
         (fun e -> stop e || fail e) [] right
      let aff, restr =
        parse state (function T ("elif" | "else")::_ -> true | k when stop k -> true | t'::_ -> not (t.IndentedLess t'))
         fail [] restr
      let neg, restr =
        match restr with
        |T "elif"::restr | (T "else"::T "if"::restr & ConsqS) ->
          parse state stop fail [] (t::restr)
        |T "else"::restr ->
          parse state (function k when stop k -> true | t'::_ -> not (t.IndentedLess t') | _ -> false)
           fail [] restr
        |_ -> Token("()", t.Indentation), restr
      let parsed = Token("if", t.Indentation, [cond; aff; neg])
      Some (parse state stop fail restl (parsed::restr))
    |_ -> None
  and (|Do|_|) state stop fail = function
    |T "do" as t::restl, right ->
      Some (
        parse state (function e when stop e -> true | t'::_ -> not (t.IndentedLess t') | _ -> false)
         fail restl right
       )
    |_ -> None
  and (|While|_|) state stop fail = function
    |T "while" as t::restl, right ->
      let cond, T "do"::restr =
        parse state (function T "do"::_ -> true | _ -> false)
         (fun e -> stop e || fail e) [] right
      let body, restr = parse state stop fail [] (Token("do", t.Indentation)::restr)
      let parsed = Token("while", t.Indentation, [cond; body])
      Some (parse state stop fail restl (parsed::restr))
    |_ -> None
  and (|For|_|) state stop fail = function
    |T "for" as t::restl, right ->
      let name, T "in"::restr =
        parse Pattern (function T "in"::_ -> true | _ -> false)
         (fun e -> stop e || fail e) [] right
      let iterable, restr =
        match parse state (function T("do" | "..")::_ -> true | _ -> false)
         (fun e -> stop e || fail e) [] restr with
        |iterable, T "do"::restr -> iterable, restr
        |a, T ".."::restr ->
          match parse state (function T("do" | "..")::_ -> true | _ -> false)
           (fun e -> stop e || fail e) [] restr with
          |b, T "do"::restr -> Token("..", [a; Token("1", []); b]), restr
          |step, T ".."::restr ->
            let b, T "do"::restr =
              parse state (function T "do"::_ -> true | _ -> false)
               (fun e -> stop e || fail e) [] restr
            Token("..", [a; step; b]), restr
      let body, restr = parse state stop fail [] (Token("do", t.Indentation)::restr)
      let parsed = Token("for", t.Indentation, [name; iterable; body])
      Some (parse state stop fail restl (parsed::restr))
    |_ -> None
  and (|Let|_|) state stop fail = function
    |((T "let" as t::restl, T ("rec" as s)::right) & ConsqLR)
    |(T ("let" as s) as t::restl, right) ->
      let name, T "="::restr =
        parse Pattern (function T "="::_ -> true | _ -> false)
         (fun e -> stop e || fail e) [] right
      let body, restr =
        parse state (function e when stop e -> true | t'::_ -> not (t.IndentedLess t') | _ -> false)
         fail [] restr
      let parsed = Token((if s = "let" then s else "let rec"), t.Indentation, [name; body])
      Some (parse state stop fail restl (parsed::restr))
    |_ -> None
  and (|Fun|_|) state stop fail = function
    |T "fun" as t::restl, right ->
      let name, T "->"::restr =
        parse Pattern (function T "->"::_ -> true | _ -> false)
         (fun e -> stop e || fail e) [] right
      let body, restr =
        parse state (function e when stop e -> true | t'::_ -> not (t.IndentedLess t') | _ -> false)
         fail [] restr
      let parsed = Token("fun", t.Indentation, [name; body])
      Some (parse state stop fail restl (parsed::restr))
    |_ -> None
  and (|PatternCase|_|) state stop fail = function
    |T "|" as t::restl, right ->
      let pattern, T "->"::restr =
        parse Pattern (function T "->"::_ -> true | _ -> false)
         (fun e -> stop e || fail e)    //buggy infixes: test      a+b; |c & d ->        (maybe move infixes up)
         [] right
      let body, restr =
        parse state (function e when stop e -> true | t'::_ -> not (t.IndentedLess t') | _ -> false)
         fail [] restr
      let parsed = Token("pattern", t.Indentation, [pattern; body])
      Some (parse state stop fail restl (parsed::restr))
    |_ -> None
  and (|Match|_|) state stop fail = function
    |T "match" as t::restl, right ->
      let target, T "with"::restr =
        parse Pattern (function T "with"::_ -> true | _ -> false)
         (fun e -> stop e || fail e) restl right
      let restr =
        match restr with
        |T "|"::_ -> restr
        |_ -> Token("|", t.Indentation)::restr
      let cases, restr = parse state stop fail [] restr
      let parsed = Token("match", t.Indentation, [target; cases])
      Some (parse state stop fail restl (parsed::restr))
    |_ -> None
  and (|Function|_|) state stop fail = function
    |T "function" as t::restl, right ->
      let restr =
        match right with
        |T "|"::_ -> right
        |_ -> Token("|", t.Indentation)::right
      let cases, restr = parse state stop fail [] restr
      let parsed = Token("function", t.Indentation, [cases])
      Some (parse state stop fail restl (parsed::restr))
    |_ -> None
  and (|Dot|_|) state stop fail = function
    |T "."::(A as a)::restl, (A as b)::restr ->  //not perfect: catches bracketed expressions as weird dotted accesses
      let parsed = Token("dot", a.Indentation, true, [a; b])
      Some (parse state stop fail restl (parsed::restr))
    //fix priority
    |left, a::(T "." as t)::restr -> Some (parse state stop fail (t::a::left) restr)
    |_ -> None
  and (|Infix|_|) state stop fail = function
    |left:Token list, (T _ as nfx)::restr when nfx.Priority > -1 ->
      let a::restl =
        match left with
        |[] -> failwith "infix applied, but no preceding argument"
        |_ -> left
      let b,restr =
        parse state (function nfx'::_ when nfx.EvaluatedFirst nfx' -> true | k -> stop k)
         fail [] restr
      match b.Name, b.Dependants with
      |"sequence", parsed::r ->
        let parsed = Token("apply", a.Indentation, [Token("apply", a.Indentation, [nfx; a]); parsed])
        Some (parse state stop fail restl (parsed::r @ restr))
      |_ -> failwith "infix failed"
    |_ -> None
  and (|Tuple|_|) state stop fail = function      //testing needed
    |t::restl, T ","::restr ->
      let parsed, restr =
        match parse state stop fail [] restr with
        |X(",", es), restr -> Token(",", t::es), restr
        |parsed, restr -> Token(",", [t; parsed]), restr
      Some (parse state stop fail (parsed::restl) restr)
    |_ -> None
  and (|Apply|_|) state stop fail = function
    |(A as a)::restl, (A as b)::restr when a.IndentedLess b ->
      let restr = Token("apply", a.Indentation, true, [a; b])::restr
      Some (parse state stop fail restl restr)
    |_ -> None
  and (|Transfer|_|) state stop fail = function
    |left, x::restr -> Some (parse state stop fail (x::left) restr)
    |_ -> None
  and (|PatternBrackets|_|) state stop fail = function
    |left, (T "(" as t)::restr ->
      let parsed, T ")"::restr =
        match restr with
        |T ")"::_ -> Token("()", t.Indentation, true, []), restr
        |_ -> parse Pattern (function T ")"::_ -> true | _ -> false) (fun _ -> false) [] restr
      let parsed = Token("()", t.Indentation, true, [parsed])
      Some (parse Pattern stop fail left (parsed::restr))
    |_ -> None
  and (|PatternComb|_|) state stop fail = function
    |left:Token list, (T ("|" | "&" as s) as nfx)::restr ->
      let a::restl =
        match left with
        |[] -> failwith "pattern infix rule applied with no preceeding argument"
        |_ -> left
      let b, restr =
        parse Pattern (function T ("|" | "&")::_ -> true | k -> stop k) fail [] restr
      let parsed = Token(s, a.Indentation, [a; b])
      Some (parse Pattern stop fail restl (parsed::restr))
    |_ -> None

  let parseSyntax =
    preprocess
     >> parse Normal (function [] -> true | _ -> false) (fun _ -> false) []
     >> fst

module Python2 =
  let preprocess: string list->Token list list =
    FSharp.preprocess
     >> List.fold (fun acc e ->
          match acc with
          |(hd:Token::tl1)::tl2 when fst e.Indentation = fst hd.Indentation -> (e::hd::tl1)::tl2
          |_ -> [e]::acc
         ) []
     >> List.map List.rev
     >> List.rev
  let indent (e:Token) = snd e.Indentation
  let rec parseLineByLine left right =
    match (left:Token list), (right:Token list) with
    |ExprWithBlock & (X(s, dep) as a::_, b::_) when indent a < indent b ->
      let parsed, restr = parseLineByLine [] right
      Token(s, (0, indent a), dep @ [parsed]), restr
    |ExprWithBlock -> failwith "block needs an expression"
    |a::restl, b::restr when indent a = indent b ->
      parseLineByLine (b::a::restl) restr
    |a::restl, b::restr when indent a > indent b ->
      Token("sequence", (0, indent a), left), right
    |_ -> failwith "indented block doesn't belong there"
  and (|ExprWithBlock|_|) = function   //consider making a generalization for all the colon keywords (put them all in a list)
    |(X("if", ([_] as dep)) | X("for", ([_; _] as dep)) | X("while", ([_] as dep)) as t)
      ::_, _ ->
      Some ExprWithBlock
    |_ -> None
  let rec parseLine stop fail left right =
    match (left:Token list), (right:Token list) with
    |_ when stop right ->
      let col =
        match left, right with
        |a::_, _ | _, a::_ -> indent a
        |[], [] -> failwith "oh no a blank line"
      Token("sequence", (0, col), left), right
    |_ when fail right -> failwithf "unexpected end of context (in line) %O" right
    |Bracket stop fail x
    |ExprWithColon stop fail x
    |Apply stop fail x
    |Tuple stop fail x
    |Transfer stop fail x
                             -> x
    |_, [] -> Token("()", (0,0)), []
  and (|Bracket|_|) stop fail = function
    |T "("::restl, right ->
      let parsed, T ")"::restr =
        parseLine (function T ")"::_ -> true | _ -> false) (fun e -> stop e || fail e)
         [] right
      Some (parseLine stop fail (parsed::restl) restr)
    |_ -> None
  and (|ExprWithColon|_|) stop fail = function   //consider making a generalization for all the colon keywords
    |T("if" | "while" | "for" as s) as t::restl, right ->
      let beforeColon, restr =
        match s with
        |"for" ->
          let namePattern, T "in"::restr =     //maybe a different state here
            parseLine (function T "in"::_ -> true | _ -> false) (fun e -> stop e || fail e)
             [] right
          let iterable, T ":"::restr =
            parseLine (function T ":"::_ -> true | _ -> false) (fun e -> stop e || fail e)
             [] right
          [namePattern; iterable], restr
        |_ ->
          let cond, T ":"::restr =
            parseLine (function T ":"::_ -> true | _ -> false) (fun e -> stop e || fail e)
             [] right
          [cond], restr
      let afterColon =
        match restr with
        |[] -> []
        |_ ->
          let body, restr =     // restr = []
            parseLine (function [] -> true | _ -> false) (fun e -> stop e || fail e)
             [] restr
          match s with
          |"if" -> [body; Token("pass", [])]
          |_ -> [body]
      Some (parseLine stop fail (Token(s, (0, indent t), beforeColon @ afterColon)::restl) [])
    |_ -> None
  //todo: operators, dot expressions, function calls, commands (eg. print)
  and (|Apply|_|) stop fail = function
    |a::restl, (T "("::_ as right) ->
      let argsTuple, restr = parseLine stop fail [] right
      Some (parseLine stop fail (Token("apply", (0, indent a), [a; argsTuple])::restl) restr)
    |_ -> None
  and (|Tuple|_|) stop fail = function
    |t::restl, T ","::restr ->
      let parsed, restr =
        match parseLine stop fail [] restr with
        |X(",", es), restr -> Token(",", (0, indent t), t::es), restr
        |parsed, restr -> Token(",", (0, indent t), [t; parsed]), restr
      Some (parseLine stop fail (parsed::restl) restr)
    |_ -> None
  and (|Transfer|_|) stop fail = function
    |left, x::restr -> Some (parseLine stop fail (x::left) restr)
    |_ -> None
