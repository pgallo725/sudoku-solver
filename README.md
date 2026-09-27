# sudoku-solver

A fast command-line Sudoku solver written in C#/.NET 10.

The code was hand-optimized using bit manipulation and SIMD intrinsics, to the point where it can solve even complex puzzles in just a few microseconds on a modern CPU.

I was laying on the beach and getting bored, when I noticed a lady nearby that was doing a Sudoku.
I started thinking of a fast algorithm to solve the puzzle and implemented this as soon as I got home.

## Usage

```sh
USAGE:
    sudoku-solver.exe <path>

ARGUMENTS:
    <path>  The path to a text file with the sudoku definition
```

Pass the path to a Sudoku puzzle file containing nine rows of nine characters.
Use digits `1`–`9` for given values and `-` for empty cells. For example:

```text
--2-3--51
-5-7---8-
7-6-8--3-
9--3-6--8
-7----92-
1-8--2---
53---48--
-----35-7
-6-52---4
```

Some sample puzzles of varying difficulty are included in the `examples` folder in this repository.
