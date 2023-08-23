using ChessChallenge.API;
using System;
using System.Collections.Generic;
using System.Linq;

public class MyBot : IChessBot
{
    private const double MinCheckmateValue = 1000;

    // "static" costs a token, but "readonly" doesn't?
    private readonly double[] PieceValues = new[]
    {
        0,
        1,
        3,
        3,
        5,
        9,
        MinCheckmateValue,
    };

    private Random _random = new Random();
    private Dictionary<ulong, Move> _hashMoves = new();
    private Dictionary<(int Depth, ulong BoardHash), (double Score, Move Move, bool Pog, bool AntiPog)> _transpositionTable = new();
    private Dictionary<ulong, double> _evalCache = new();

#if DEBUG
    private int _nodesSearched = 0;

    private int _evalCacheHits = 0;
    private int _evalCacheMisses = 0;

    private int _transpositionTableHits = 0;
    private int _transpositionTableCutoffs = 0;
#endif

    public Move Think(Board board, Timer timer)
    {
#if DEBUG
        _evalCacheHits = 0;
        _evalCacheMisses = 0;
        _evalCache.Clear();
#endif
        var depth = 1;
        var move = Move.NullMove;
        var score = 0D;
        var timeout = TimeSpan.FromMilliseconds(Math.Min(Math.Max(timer.MillisecondsRemaining - 2000, 25), 2000));
        var sameMoveCounter = 0;

        _hashMoves.Clear();
        try
        {
            while (Math.Abs(score) < MinCheckmateValue && (sameMoveCounter < 2 || timer.MillisecondsElapsedThisTurn < 250))
            {
#if DEBUG
                _nodesSearched = 0;
                _transpositionTableHits = 0;
                _transpositionTableCutoffs = 0;
#endif
                _transpositionTable.Clear();
                var (newMove, newScore) = Minimax(board, timer, timeout, board.IsWhiteToMove, depth++);
#if DEBUG
                Console.WriteLine($"{depth - 1, -3} {_nodesSearched, -8} {newMove, -12} {_transpositionTableCutoffs}/{_transpositionTableHits}");
#endif

                sameMoveCounter = newMove == move ? sameMoveCounter + 1 : 0;

                move = newMove;
                score = newScore;
            }
        }
        catch (TimeoutException)
        {
#if DEBUG
            Console.WriteLine("(timed out)");
#endif
        }

#if DEBUG
        Console.WriteLine($"Eval cache hits: {_evalCacheHits * 100D / (_evalCacheHits + _evalCacheMisses):F2}%");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"{move,-12} {score:F4}");
        Console.ResetColor();
#endif

        return move;
    }

    private (Move Move, double Score) Minimax(Board board, Timer timer, TimeSpan timeout, bool playingAsWhite, int maxDepth, int depth = 0, double bestMax = double.MinValue, double bestMin = double.MaxValue, Move previousMove = default)
    {
        if (timer.MillisecondsElapsedThisTurn >= timeout.TotalMilliseconds)
            throw new TimeoutException();

#if DEBUG
        _nodesSearched++;
#endif

        var isOurTurn = !(playingAsWhite ^ board.IsWhiteToMove);

        // why make me write if statements like this
        if (board.IsInCheckmate())
            return (Move.NullMove, (isOurTurn ? -MinCheckmateValue : MinCheckmateValue) * (maxDepth - depth + 1));
        else if (board.IsDraw())
            return (Move.NullMove, 0);
        else if (depth >= maxDepth)
            return (Move.NullMove, BoardEvaluation(board, playingAsWhite));

        var transpositionTableKey = (depth, board.ZobristKey);
        Move priorityMove;

        if (_transpositionTable.TryGetValue(transpositionTableKey, out var record))
        {
#if DEBUG
            _transpositionTableHits++;
            _transpositionTableCutoffs++;
#endif

            if (record.Pog)
                bestMin = Math.Min(bestMin, record.Score);
            else if (record.AntiPog)
                bestMax = Math.Max(bestMax, record.Score);
            else
                return (record.Move, record.Score);

#if DEBUG
            _transpositionTableCutoffs--;
#endif

            priorityMove = record.Move;
        }
        else
            _hashMoves.TryGetValue(board.ZobristKey, out priorityMove);

        var bestMove = Move.NullMove;
        var bestMoveScore = isOurTurn ? double.MinValue : double.MaxValue;

        bool pog = false, antiPog = false;

        foreach (var move in board.GetLegalMoves()
            .OrderByDescending(x => EstimateMoveImportance(x))
            .OrderByDescending(x => priorityMove == x))
        {
            board.MakeMove(move);
            try
            {
                var eval = BoardEvaluation(board, playingAsWhite);

                var enshallow = !move.IsCapture
                    && !move.IsPromotion;
                    //&& !(move.MovePieceType == PieceType.Pawn && IsPassedPawn(board, move.TargetSquare, !board.IsWhiteToMove));
                
                var extend = move.IsCapture && previousMove.IsCapture && move.TargetSquare == previousMove.TargetSquare
                    || board.IsInCheck();
                
                var (_, score) = Minimax(
                    board,
                    timer,
                    timeout,
                    playingAsWhite,
                    extend ? maxDepth + 1 : (enshallow ? maxDepth - depth / 3 : maxDepth),
                    depth: depth + 1,
                    bestMax: bestMax,
                    bestMin: bestMin,
                    previousMove: move);

                if (isOurTurn)
                {
                    if (score >= bestMin)
                    {
                        pog = true;
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
                        antiPog = true;
                        bestMove = move;
                        bestMoveScore = score;
                        break;
                    }
                    bestMin = Math.Min(score, bestMin);
                }
                if (isOurTurn && score > bestMoveScore || !isOurTurn && score < bestMoveScore)
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

        _hashMoves[board.ZobristKey] = bestMove;
        _transpositionTable[transpositionTableKey] = (bestMoveScore, bestMove, pog, antiPog);

        return (bestMove, bestMoveScore);
    }

    // We value:
    //  - having pieces (obviously)
    //  - how many squares are threatened on the board
    //  - not having threatened undefended pieces (such pieces are not counted in material cost)
    //  - for pawns: being close to the opposite rank
    //  - for all but pawns: being close to the enemy king
    private double BoardEvaluation(Board board, bool playingAsWhite)
    {
#if DEBUG
        _evalCacheHits++;
#endif
        if (_evalCache.TryGetValue(board.ZobristKey, out var cachedEval))
            return cachedEval;
#if DEBUG
        _evalCacheHits--;
        _evalCacheMisses++;
#endif

        var evaluation = 0D;

        var isOurTurn = !(playingAsWhite ^ board.IsWhiteToMove);

        var allOurAttacksBitboard = GetColorAttacksBitboard(board, playingAsWhite);
        var allTheirAttacksBitboard = GetColorAttacksBitboard(board, !playingAsWhite);

        foreach (var piece in board.GetAllPieceLists().SelectMany(x => x))
        {
            var rank = piece.Square.Rank;
            var file = piece.Square.File;

            var pieceValue = PieceValues[(int)piece.PieceType];

            if (piece.PieceType == PieceType.Pawn)
                pieceValue *= 1 + (piece.IsWhite ? rank + 1 : 8 - rank) / 8D / 200;
            else
            {
                var enemyKingSquare = board.GetKingSquare(!piece.IsWhite);
                pieceValue *= 1 + (1 - (Math.Abs(enemyKingSquare.Rank - rank) + Math.Abs(enemyKingSquare.File - file)) / 14D) / 200;
            }

            var threatenedSquares = BitboardHelper.GetNumberOfSetBits(BitboardHelper.GetPieceAttacks(piece.PieceType, piece.Square, board, piece.IsWhite));
            if (!(playingAsWhite ^ piece.IsWhite)) // if our piece
            {
                if (isOurTurn || !BitboardHelper.SquareIsSet(~allOurAttacksBitboard & allTheirAttacksBitboard & (playingAsWhite ? board.WhitePiecesBitboard : board.BlackPiecesBitboard), piece.Square))
                {
                    evaluation += pieceValue;
                    evaluation += threatenedSquares / 500D;
                }
            }
            else if (!isOurTurn || !BitboardHelper.SquareIsSet(~allTheirAttacksBitboard & allOurAttacksBitboard & (playingAsWhite ? board.BlackPiecesBitboard : board.WhitePiecesBitboard), piece.Square))
            {
                evaluation -= pieceValue;
                evaluation -= threatenedSquares / 500D;
            }
        }

        _evalCache[board.ZobristKey] = evaluation;

        return evaluation;
    }

    private double EstimateMoveImportance(Move move)
    {
        var captureFactor = move.IsCapture ? PieceValues[(int)move.CapturePieceType] : 0;
        var pieceValueFactor = move.MovePieceType == PieceType.King ? 0 : PieceValues[(int)move.MovePieceType];
        return 10000 * captureFactor + 10 * pieceValueFactor + _random.NextDouble();
    }

    /*
    private bool IsPassedPawn(Board board, Square square, bool isWhite)
    {
        var rank = square.Rank;
        while (rank >= 0 && rank < 8)
        {
            rank += isWhite ? 1 : -1;
            if (BitboardHelper.SquareIsSet(board.AllPiecesBitboard, new Square(square.File, rank)))
                return false;
        }
        return true;
    }
    */
    private ulong GetColorAttacksBitboard(Board board, bool white)
    {
        var result = 0UL;
        foreach (var pieceList in board.GetAllPieceLists())
        {
            if (pieceList.IsWhitePieceList ^ white)
                continue;
            foreach (var piece in pieceList)
                result |= BitboardHelper.GetPieceAttacks(piece.PieceType, piece.Square, board, white);
        }

        return result;
    }
}