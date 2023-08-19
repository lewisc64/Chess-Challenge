using ChessChallenge.API;
using System;
using System.Collections.Generic;
using System.Linq;

public class MyBot : IChessBot
{
    private const int PanicThresholdMilliseconds = 2000;
    private const int MinThinkMilliseconds = 100;
    private const int MaxThinkMilliseconds = 2000;

    private const int MinCheckmateValue = 1000;

    private static readonly Dictionary<PieceType, double> PieceValues = new()
    {
        { PieceType.Pawn, 1 },
        { PieceType.Knight, 3 },
        { PieceType.Bishop, 3 },
        { PieceType.Rook, 5 },
        { PieceType.Queen, 9 },
    };

    private Random _random = new Random();
    private int _moveNumber = 0;
    private bool _skipOpening = false;
    private Dictionary<int, Move> _killerMoves = new();

    public Move Think(Board board, Timer timer)
    {
        if (++_moveNumber == 1 && board.WhitePiecesBitboard != 65535 && board.BlackPiecesBitboard != 18446462598732840960)
        {
            _moveNumber = 50; // this isn't a normal start!
        }

        // Wayward queen attack. Not likely to trick an engine, but fun.
        if (!_skipOpening && _moveNumber <= 2)
        {
            if (!board.GetPieceList(PieceType.Queen, board.IsWhiteToMove).Any())
            {
                _skipOpening = true;
            }
            else if (_moveNumber == 1)
            {
                if (!board.IsWhiteToMove && board.SquareIsAttackedByOpponent(new Square("e5")))
                {
                    _skipOpening = true;
                }
                else
                {
                    return new Move(board.IsWhiteToMove ? "e2e4" : "e7e5", board);
                }
            }
            else if (_moveNumber == 2)
            {
                if (board.IsWhiteToMove && board.SquareIsAttackedByOpponent(new Square("h5")) || !board.IsWhiteToMove && board.SquareIsAttackedByOpponent(new Square("h4")))
                {
                    _skipOpening = true;
                }
                else
                {
                    return new Move(board.IsWhiteToMove ? "d1h5" : "d8h4", board);
                }
            }
        }

        var depth = 1;
        Move? move = null;
        var score = 0D;
        var timeout = TimeSpan.FromMilliseconds(Math.Min(Math.Max(timer.MillisecondsRemaining - PanicThresholdMilliseconds, 25), MaxThinkMilliseconds));
        var sameMoveCounter = 0;

        while (Math.Abs(score) >= MinCheckmateValue || sameMoveCounter < 3 || timer.MillisecondsElapsedThisTurn < MinThinkMilliseconds)
        {
            Move? newMove;
            double newScore;

            Console.WriteLine($"Searching to a maximum depth of {depth}");
            try
            {
                (newMove, newScore) = Minimax(board, timer, timeout, board.IsWhiteToMove, depth++);
            }
            catch (TimeoutException)
            {
                break;
            }

            sameMoveCounter += newMove == move ? 1 : 0;

            move = newMove;
            score = newScore;
        }

        Console.WriteLine(board.GetFenString());
        Console.WriteLine($"{move}, score: {score}");

        return move!.Value;
    }

    private (Move? Move, double Score) Minimax(Board board, Timer timer, TimeSpan timeout, bool playingAsWhite, int maxDepth, int depth = 0, double bestMax = double.MinValue, double bestMin = double.MaxValue)
    {
        if (depth >= 4 && timer.MillisecondsElapsedThisTurn >= timeout.TotalMilliseconds)
        {
            throw new TimeoutException();
        }

        var isOurTurn = !(playingAsWhite ^ board.IsWhiteToMove);

        if (board.IsInCheckmate())
        {
            return (null, isOurTurn ? -MinCheckmateValue * (maxDepth - depth + 1) : MinCheckmateValue * (maxDepth - depth + 1));
        }
        else if (board.IsDraw())
        {
            return (null, 0);
        }
        else if (depth >= maxDepth)
        {
            return (null, BoardEvaluation(board, playingAsWhite));
        }

        Move? bestMove = null;
        var bestMoveScore = isOurTurn ? double.MinValue : double.MaxValue;

        var moves = board.GetLegalMoves().OrderByDescending(x => EstimateMoveImportance(x, _moveNumber + depth));

        foreach (var move in moves)
        {
            board.MakeMove(move);
            try
            {
                var (_, score) = Minimax(
                    board,
                    timer,
                    timeout,
                    playingAsWhite,
                    maxDepth,
                    depth: depth + 1,
                    bestMax: bestMax,
                    bestMin: bestMin);

                if (isOurTurn)
                {
                    if (score >= bestMin)
                    {
                        bestMove = move;
                        bestMoveScore = score;
                        break;
                    }
                    bestMax = Math.Max(score, bestMax);
                }
                else
                {
                    if (score <= bestMax)
                    {
                        bestMove = move;
                        bestMoveScore = score;
                        break;
                    }
                    bestMin = Math.Min(score, bestMin);
                }
                if (bestMove is null || isOurTurn && score > bestMoveScore || !isOurTurn && score < bestMoveScore)
                {
                    bestMoveScore = score;
                    bestMove = move;
                }
            }
            finally
            {
                board.UndoMove(move);
            }
        }

        _killerMoves[depth + _moveNumber] = bestMove!.Value;
        return (bestMove, bestMoveScore);
    }

    // We value:
    //  - having pieces (obviously)
    //  - how many squares are threatened on the board
    private double BoardEvaluation(Board board, bool playingAsWhite)
    {
        var evaluation = 0D;

        for (var rank = 0; rank < 8; rank++)
        {
            for (var file = 0; file < 8; file++)
            {
                if (PiecePresentInBitboard(board.AllPiecesBitboard, file, rank))
                {
                    var piece = board.GetPiece(new Square(file, rank));

                    if (piece.PieceType != PieceType.King)
                    {
                        var pieceValue = PieceValues[piece.PieceType];
                        var threatenedSquares = CountSetBits(BitboardHelper.GetPieceAttacks(piece.PieceType, piece.Square, board, piece.IsWhite));

                        if (PiecePresentInBitboard(playingAsWhite ? board.WhitePiecesBitboard : board.BlackPiecesBitboard, file, rank))
                        {
                            evaluation += pieceValue;
                            evaluation += threatenedSquares / 500D;
                        }
                        else
                        {
                            evaluation -= pieceValue;
                            evaluation -= threatenedSquares / 500D;
                        }
                    }
                }
            }
        }

        return evaluation;
    }

    private double EstimateMoveImportance(Move move, int turnNumber)
    {
        if (_killerMoves.ContainsKey(turnNumber) && _killerMoves[turnNumber] == move)
        {
            return double.MaxValue;
        }

        var captureFactor = move.IsCapture ? PieceValues[move.CapturePieceType] : 0;
        var pieceValueFactor = move.MovePieceType == PieceType.King ? 0 : PieceValues[move.MovePieceType];
        return 10000 * captureFactor + 100 * pieceValueFactor + _random.NextDouble();
    }

    private static bool PiecePresentInBitboard(ulong bitboard, int file, int rank)
    {
        return (bitboard & 1UL << (rank * 8 + file)) != 0;
    }

    private static int CountSetBits(ulong n)
    {
        var count = 0;
        while (n != 0)
        {
            count++;
            n &= n - 1;
        }
        return count;
    }
}