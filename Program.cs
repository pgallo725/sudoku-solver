using Spectre.Console;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;


namespace SudokuSolver
{
    public struct Statistics(/* default constructed */)
    {
        public uint   total_steps           = 0;
        public uint   speculative_moves     = 0;
        public uint   speculative_rollbacks = 0;
        public double total_time            = 0.0;
    }


    [StructLayout(LayoutKind.Sequential, Pack=32)]  // 32 byte aligned
    public struct SudokuBoard
    {
        // --------------------------------------------------------------------
        // LOOKUP TABLES (initialized in the static constructor)
        //  * BitCountLookup: for each value in [0, 511] stores the number of bits set to 1 in its binary form
        //  * BitExpansionLookup: for each value in [0, 511] stores a Vector128<byte> where each bit in
        //                        the binary representation of the original value is expanded to a byte

        private static readonly byte[]            BitCountLookup;       
        private static readonly Vector128<byte>[] BitExpansionLookup;
        // --------------------------------------------------------------------

        [Flags]
        public enum CellBits : ushort   // 16 bits per cell
        {
            None       = 0,      // 0000000000000000
            Number1    = 0x01,   // 0000000000000001
            Number2    = 0x02,   // 0000000000000010
            Number3    = 0x04,   // 0000000000000100
            Number4    = 0x08,   // 0000000000001000
            Number5    = 0x10,   // 0000000000010000
            Number6    = 0x20,   // 0000000000100000
            Number7    = 0x40,   // 0000000001000000
            Number8    = 0x80,   // 0000000010000000
            Number9    = 0x100,  // 0000000100000000
            AllNumbers = 0x1FF,  // 0000000111111111
            IsSolved   = 0x8000  // 1000000000000000
        }

        [InlineArray(9*9)]
        private struct CellGrid
        {
            private CellBits cell;
        }
        private CellGrid grid;   // 18 bytes per row, 162 bytes total

        private ushort unsolved = 9*9;  // start with all cells as 'unsolved'

        private enum State
        {
            Progress,
            Stuck,
            Invalid,
            Solved
        }


        /// <summary>
        /// Initialize static lookup tables.
        /// </summary>
        static SudokuBoard()
        {
            BitCountLookup = new byte[512];
            for (int value = 1; value < BitCountLookup.Length; value++)
                BitCountLookup[value] = (byte)(BitCountLookup[value >> 1] + (value & 1));

            BitExpansionLookup = new Vector128<byte>[512];
            for (ushort value = 0; value < BitExpansionLookup.Length; value++)
            {
                BitExpansionLookup[value] = Vector128.Create(
                    (byte)((value & (ushort)CellBits.Number1) != 0 ? 1 : 0),
                    (byte)((value & (ushort)CellBits.Number2) != 0 ? 1 : 0),
                    (byte)((value & (ushort)CellBits.Number3) != 0 ? 1 : 0),
                    (byte)((value & (ushort)CellBits.Number4) != 0 ? 1 : 0),
                    (byte)((value & (ushort)CellBits.Number5) != 0 ? 1 : 0),
                    (byte)((value & (ushort)CellBits.Number6) != 0 ? 1 : 0),
                    (byte)((value & (ushort)CellBits.Number7) != 0 ? 1 : 0),
                    (byte)((value & (ushort)CellBits.Number8) != 0 ? 1 : 0),
                    (byte)((value & (ushort)CellBits.Number9) != 0 ? 1 : 0),
                    0, 0, 0, 0, 0, 0, 0);
            }
        }

        /// <summary>
        /// Initialize an empty sudoku board.
        /// </summary>
        public SudokuBoard()
        {
            ((Span<CellBits>)this.grid).Fill(CellBits.AllNumbers);
            unsolved = 9*9;
        }
        /// <summary>
        /// Initialize a copy of an existing sudoku board.
        /// </summary>
        /// <param name="other">The sudoku board to be copied into the new instance.</param>
        public SudokuBoard(in SudokuBoard other)
        {
            Copy(other);
        }

        /// <summary>
        /// Copy data from another sudoku board.
        /// </summary>
        /// <param name="other">The sudoku board to be copied into the target instance.</param>
        public void Copy(in SudokuBoard other)
        {
            ((ReadOnlySpan<CellBits>)other.grid).CopyTo(this.grid);
            unsolved = other.unsolved;
        }


        /// <summary>
        /// Check if the sudoku board is fully solved.
        /// </summary>
        public readonly bool IsSolved()
        {
            return (unsolved == 0);
        }

        /// <summary>
        /// Check if a cell of the sudoku board has a valid number assigned to it.
        /// </summary>
        public readonly bool IsCellSolved(int row, int col)
        {
            return (grid[row*9 + col] & CellBits.IsSolved) != 0;
        }


        /// <summary>
        /// Get the value stored in a cell of the sudoku board.
        /// </summary>
        /// <returns>A number in [1, 9] if the cell is not empty, otherwise 0.</returns>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public readonly ushort GetValue(int row, int col)
        {
            if (row < 0 || row >= 9)
                throw new ArgumentOutOfRangeException(nameof(row));
            if (col < 0 || col >= 9)
                throw new ArgumentOutOfRangeException(nameof(col));

            return (grid[row*9 + col]) switch
            {
                (CellBits.IsSolved | CellBits.Number1) => 1,
                (CellBits.IsSolved | CellBits.Number2) => 2,
                (CellBits.IsSolved | CellBits.Number3) => 3,
                (CellBits.IsSolved | CellBits.Number4) => 4,
                (CellBits.IsSolved | CellBits.Number5) => 5,
                (CellBits.IsSolved | CellBits.Number6) => 6,
                (CellBits.IsSolved | CellBits.Number7) => 7,
                (CellBits.IsSolved | CellBits.Number8) => 8,
                (CellBits.IsSolved | CellBits.Number9) => 9,
                _ => 0,
            };
        }

        /// <summary>
        /// Store a value in [1, 9] into an empty cell of the sudoku board.
        /// </summary>
        /// <returns>True if successful, otherwise false.</returns>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public bool SetValue(int row, int col, ushort value)
        {
            if (row < 0 || row >= 9)
                throw new ArgumentOutOfRangeException(nameof(row));
            if (col < 0 || col >= 9)
                throw new ArgumentOutOfRangeException(nameof(col));
            if (value < 1 || value > 9)
                throw new ArgumentOutOfRangeException(nameof(value));

            if (IsCellSolved(row, col))
                return false; // cannot assign to an already solved cell
            else if ((grid[row * 9 + col] & (CellBits)(1u << value - 1)) == 0)
                return false; // the number assigned is not in the list of possible candidates

            SetValue_Internal(row, col, value);
            return true;
        }

        /// <summary>
        /// Store a value into a cell of the sudoku board and update the
        ///  candidates for other cells in the same row / column / block.<br/>
        /// It's faster than SetValue() because it does not perform any checks.
        /// </summary>
        private void SetValue_Internal(int row, int col, ushort value)
        {
            CellBits numberBit = (CellBits)(1u << value - 1);
            grid[row * 9 + col] = CellBits.IsSolved | numberBit;
            unsolved -= 1;

            // Update candidates for other cells within the same row
            for (int cell = row * 9; cell < (row + 1) * 9; cell++)
            {
                if ((grid[cell] & CellBits.IsSolved) == 0)
                    grid[cell] &= ~numberBit;
            }
            // Update candidates for other cells within the same column
            for (int cell = col; cell < 81; cell += 9)
            {
                if ((grid[cell] & CellBits.IsSolved) == 0)
                    grid[cell] &= ~numberBit;
            }

            // Update candidates for other cells within the same block
            // NOTE: 5 out of 9 were already updated by the row / column loops
            //   Here I unrolled the code to update the remaining 4 cells
            int row_1 = (row / 3) * 3 + (row % 3 + 1) % 3;
            int row_2 = (row / 3) * 3 + (row % 3 + 2) % 3;
            int column_1 = (col / 3) * 3 + (col % 3 + 1) % 3;
            int column_2 = (col / 3) * 3 + (col % 3 + 2) % 3;
            int cell_11 = row_1 * 9 + column_1;
            int cell_12 = row_1 * 9 + column_2;
            int cell_21 = row_2 * 9 + column_1;
            int cell_22 = row_2 * 9 + column_2;
            if ((grid[cell_11] & CellBits.IsSolved) == 0)
                grid[cell_11] &= ~numberBit;
            if ((grid[cell_12] & CellBits.IsSolved) == 0)
                grid[cell_12] &= ~numberBit;
            if ((grid[cell_21] & CellBits.IsSolved) == 0)
                grid[cell_21] &= ~numberBit;
            if ((grid[cell_22] & CellBits.IsSolved) == 0)
                grid[cell_22] &= ~numberBit;
        }


        /// <summary>
        /// Update the given Spectre.Console table with the sudoku grid contents.
        /// </summary>
        /// <param name="table">The table to be updated (must have exactly 9x9 cells).</param>
        /// <exception cref="ArgumentException"></exception>
        public readonly void UpdateTableDisplay(Table table)
        {
            if (table.Columns.Count != 9 || table.Rows.Count != 9)
                throw new ArgumentException("The provided table does not have 9x9 cells.", nameof(table));

            for (int row = 0; row < 9; row++)
            {
                for (int col = 0; col < 9; col++)
                {
                    ushort value = GetValue(row, col);
                    table.UpdateCell(row, col, value != 0 ? value.ToString() : " ");
                }
            }
        }


        private State UpdateBoard()
        {
            bool progress = false;

            // ---------------------------------------------------------------------------------
            // Method 1. Check candidates for each cell: if any cell has only one candidate
            //  then that number can be assigned immediately and the cell becomes solved

            for (int cell = 0; cell < 9*9; cell++)
            {
                CellBits bits = grid[cell];

                if ((bits & CellBits.IsSolved) != 0) // skip solved cells
                    continue;

                if (bits == CellBits.None) // sudoku board with no solution
                    return State.Invalid;

                // Get the number of candidates for this cell
                int count = BitCountLookup[(ushort)bits];
                if (count == 1)
                {
                    ushort number = bits switch
                    {
                        CellBits.Number1 => 1,
                        CellBits.Number2 => 2,
                        CellBits.Number3 => 3,
                        CellBits.Number4 => 4,
                        CellBits.Number5 => 5,
                        CellBits.Number6 => 6,
                        CellBits.Number7 => 7,
                        CellBits.Number8 => 8,
                        CellBits.Number9 => 9,
                        _ => 0
                    };

                    SetValue_Internal(cell/9, cell%9, number);
                    progress = true;
                }
            }
            // ---------------------------------------------------------------------------------

            if (unsolved == 0)
                return State.Solved;
            else if (progress) // method 2 (below) is more expensive, run it only when there is no progress
                return State.Progress;

            // ---------------------------------------------------------------------------------
            // Method 2. Check cells available for each number: if any row / column / block has
            //  only one cell available for one of the missing numbers, we can assign that cell

            for (int row = 0; row < 9; row++)
            {
                Vector128<byte> vec_counts = Vector128<byte>.Zero;
                Vector128<byte> vec_columns = Vector128<byte>.Zero;

                for (int col = 0; col < 9; col++)
                {
                    CellBits bits = grid[row * 9 + col];
                    if ((bits & CellBits.IsSolved) != 0)  // skip solved cells
                        continue;

                    Vector128<byte> vec_count = BitExpansionLookup[(ushort)bits];
                    Vector128<byte> vec_column = Vector128.Create((byte)col);
                    Vector128<byte> vec_mask = Vector128.Equals(vec_count, Vector128<byte>.One);

                    vec_counts += vec_count;
                    vec_columns = Vector128.ConditionalSelect(vec_mask, vec_column, vec_columns);
                }

                int n = Vector128.Count<byte>(vec_counts, 1);
                for (int i = 0; i < n; i++)
                {
                    int index = Vector128.IndexOf<byte>(vec_counts, 1);
                    vec_counts = Vector128.WithElement<byte>(vec_counts, index, 0);

                    int col = vec_columns[index];
                    SetValue_Internal(row, col, (ushort)(index + 1));
                    progress = true;
                }
            }

            for (int col = 0; col < 9; col++)
            {
                Vector128<byte> vec_counts = Vector128<byte>.Zero;
                Vector128<byte> vec_rows = Vector128<byte>.Zero;

                for (int row = 0; row < 9; row++)
                {
                    CellBits bits = grid[row * 9 + col];
                    if ((bits & CellBits.IsSolved) != 0)  // skip solved cells
                        continue;

                    Vector128<byte> vec_count = BitExpansionLookup[(ushort)bits];
                    Vector128<byte> vec_row = Vector128.Create((byte)row);
                    Vector128<byte> vec_mask = Vector128.Equals(vec_count, Vector128<byte>.One);

                    vec_counts += vec_count;
                    vec_rows = Vector128.ConditionalSelect(vec_mask, vec_row, vec_rows);
                }

                int n = Vector128.Count<byte>(vec_counts, 1);
                for (int i = 0; i < n; i++)
                {
                    int index = Vector128.IndexOf<byte>(vec_counts, 1);
                    vec_counts = Vector128.WithElement<byte>(vec_counts, index, 0);

                    int row = vec_rows[index];
                    SetValue_Internal(row, col, (ushort)(index + 1));
                    progress = true;
                }
            }

            for (int block = 0; block < 9; block++)
            {
                Vector128<byte> vec_counts = Vector128<byte>.Zero;
                Vector128<byte> vec_cells = Vector128<byte>.Zero;

                for (int cell = 0; cell < 9; cell++)
                {
                    int row = (block / 3) * 3 + (cell / 3);
                    int col = (block % 3) * 3 + (cell % 3);

                    CellBits bits = grid[row * 9 + col];
                    if ((bits & CellBits.IsSolved) != 0)  // skip solved cells
                        continue;

                    Vector128<byte> vec_count = BitExpansionLookup[(ushort)bits];
                    Vector128<byte> vec_cell = Vector128.Create((byte)cell);
                    Vector128<byte> vec_mask = Vector128.Equals(vec_count, Vector128<byte>.One);

                    vec_counts += vec_count;
                    vec_cells = Vector128.ConditionalSelect(vec_mask, vec_cell, vec_cells);
                }

                int n = Vector128.Count<byte>(vec_counts, 1);
                for (int i = 0; i < n; i++)
                {
                    int index = Vector128.IndexOf<byte>(vec_counts, 1);
                    vec_counts = Vector128.WithElement<byte>(vec_counts, index, 0);

                    int cell = vec_cells[index];
                    int row = (block / 3) * 3 + (cell / 3);
                    int col = (block % 3) * 3 + (cell % 3);
                    SetValue_Internal(row, col, (ushort)(index + 1));
                    progress = true;
                }
            }
            // ---------------------------------------------------------------------------------

            if (unsolved == 0)
                return State.Solved;
            else return progress ? State.Progress : State.Stuck;
        }


        private readonly int FindMostConstrainedCell()
        {
            int bestCell = -1;
            int minCount = int.MaxValue;

            for (int cell = 0; cell < 9*9; cell++)
            {
                CellBits bits = grid[cell];
                if ((bits & CellBits.IsSolved) != 0)
                    continue;

                int count = BitCountLookup[(ushort)bits];
                if (count < minCount)
                {
                    minCount = count;
                    bestCell = cell;
                }
            }
            return bestCell;
        }


        public bool Solve(ref Statistics statistics)
        {
        SolverStep:
            State state = UpdateBoard();
            statistics.total_steps += 1;

            switch (state)
            {
                case State.Progress:  goto SolverStep;
                case State.Stuck:
                {
                    // Find the best candidate cell for the speculative step
                    int speculativeCell = FindMostConstrainedCell();
                    CellBits candidates = grid[speculativeCell];

                    for (ushort number = 1; number <= 9; number++)
                    {
                        if ((candidates & (CellBits)(1u << number - 1)) == 0)
                            continue;

                        // Create a temporary copy of the sudoku board and apply the speculative move
                        SudokuBoard speculativeBoard = new(this);
                        speculativeBoard.SetValue_Internal(speculativeCell / 9, speculativeCell % 9, number);
                        statistics.speculative_moves += 1;

                        // Recursively solve the speculative board
                        bool solved = speculativeBoard.Solve(ref statistics);
                        if (!solved)
                        {
                            // Go to next candidate move
                            statistics.speculative_rollbacks += 1;
                            continue;
                        }
                        this.Copy(speculativeBoard);
                        return true;
                    }
                    return false;
                }
                case State.Invalid:  return false;
                case State.Solved:   return true;
            }
            return false;
        }
    }


    public class Program
    {
        static void Main(string[] args)
        {
            // Check command line arguments format
            if (args.Length != 1)
            {
                AnsiConsole.MarkupLine("""
                    [red][[ERROR]] Invalid command-line arguments.[/]
                    
                    [lightgoldenrod2_2]USAGE:[/]
                        sudoku-solver.exe [cyan]<path>[/]

                    [lightgoldenrod2_2]ARGUMENTS:[/]
                        <path>  The path to a text file with the sudoku definition

                    """);
                return;
            }

            // Check existence of the provided input file
            if (!File.Exists(args[0]))
            {
                AnsiConsole.MarkupLineInterpolated($"""
                    [red][[ERROR]] File '{args[0]}' does not exist or is inaccessible.[/]

                    """);
                return;
            }

            // Read the sudoku definition from the input file
            SudokuBoard sudoku = new();
            try
            {
                using TextReader reader = new StreamReader(args[0]);
                for (int row = 0; row < 9; row++)
                {
                    string? line = reader.ReadLine();
                    if (line is null || line.Length > 9)
                        throw new ApplicationException();

                    for (int col = 0; col < 9; col++)
                    {
                        char c = line[col];
                        if (c == '-')
                            continue;
                        else if (c < '1' || c > '9')
                            throw new ApplicationException();
                        sudoku.SetValue(row, col, (ushort)(c - '0'));
                    }
                }
            }
            catch // any possible error
            {
                AnsiConsole.MarkupLineInterpolated($"""
                    [red][[ERROR]] File '{args[0]}' is not a valid sudoku file.[/]
                    
                    """);
                return;
            }

            var table = new Table()
                .Title("Sudoku Board")
                .AddColumn(string.Empty)
                .AddColumn(string.Empty)
                .AddColumn(string.Empty)
                .AddColumn(string.Empty)
                .AddColumn(string.Empty)
                .AddColumn(string.Empty)
                .AddColumn(string.Empty)
                .AddColumn(string.Empty)
                .AddColumn(string.Empty)
                .AddEmptyRow()
                .AddEmptyRow()
                .AddEmptyRow()
                .AddEmptyRow()
                .AddEmptyRow()
                .AddEmptyRow()
                .AddEmptyRow()
                .AddEmptyRow()
                .AddEmptyRow()
                .HideHeaders()
                .HideFooters()
                .DoubleEdgeBorder()
                .ShowRowSeparators();

            // Struct to track statistics of the solver
            Statistics stats = new();

            AnsiConsole.Write('\n');
            AnsiConsole.Live(table)
                .Start(ctx =>
                {
                    // Update table display with initial sudoku grid contents
                    sudoku.UpdateTableDisplay(table);
                    table.Caption("[grey50]Press ENTER to start the solver[/]");
                    ctx.Refresh();

                    // Wait for user input before proceeding
                    while (Console.ReadKey(true).Key != ConsoleKey.Enter) ;

                    bool success = false;
                    long timestart = Stopwatch.GetTimestamp();
                    {
                        // Start the solver
                        success = sudoku.Solve(ref stats);
                    }
                    stats.total_time = Stopwatch.GetElapsedTime(timestart).TotalMilliseconds;

                    // Update table display with solved sudoku grid contents
                    sudoku.UpdateTableDisplay(table);
                    if (success)
                        table.Caption("[green3]SOLVED![/]");
                    else table.Caption("[red]ERROR![/]");
                    ctx.Refresh();
                });

            // Display solution statistics
            var statsTable = new Table()
                .Title("[lightgoldenrod2_2]Statistics[/]")
                .BorderColor(Color.Grey30)
                .AddColumn(string.Empty)
                .AddColumn(string.Empty)
                .AddRow(["Total Steps", stats.total_steps.ToString()])
                .AddRow(["Speculative Moves", stats.speculative_moves.ToString()])
                .AddRow(["Rollbacks", stats.speculative_rollbacks.ToString()])
                .AddRow(["Total Time", stats.total_time < 1.0 ? $"{stats.total_time*1000:F1} us" : $"{stats.total_time:F3} ms"])
                .Width(37) // to match the width of the sudoku grid
                .HideHeaders()
                .HideFooters()
                .ShowRowSeparators();

            AnsiConsole.Write('\n');
            AnsiConsole.Write(statsTable);
        }
    }
}
