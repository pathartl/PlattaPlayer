using System.Runtime.CompilerServices;

namespace PlattaPlayer.Codecs.Spc.Emulation;

// The SPC700 core. Each instruction issues the same sequence of read/write/idle bus cycles as ares's
// component/processor/spc700 (instructions.cpp), since cycle timing is what keeps the DSP and timers in step.
internal sealed partial class Smp
{
    /// <summary>Test only: called before each instruction.</summary>
    internal static Action<Smp>? TraceHook;

    internal long Cycles => (_clock + _rebased) / 2;

    internal string TraceLine() =>
        $"{(_clock + _rebased) / 2,9} PC={_pc:X4} op={_ram[_pc]:X2} {_ram[(_pc + 1) & 0xffff]:X2} {_ram[(_pc + 2) & 0xffff]:X2} A={_a:X2} X={_x:X2} Y={_y:X2} SP={_s:X2} P={Psw:X2}";

    private ushort _pc;
    private byte _a, _x, _y, _s;
    private bool _c, _z, _i, _h, _b, _p, _v, _n;
    private bool _wait, _stop;

    private int YA
    {
        get => _y << 8 | _a;
        set
        {
            _a = (byte)value;
            _y = (byte)(value >> 8);
        }
    }

    private byte Psw =>
        (byte)((_c ? 0x01 : 0) | (_z ? 0x02 : 0) | (_i ? 0x04 : 0) | (_h ? 0x08 : 0)
               | (_b ? 0x10 : 0) | (_p ? 0x20 : 0) | (_v ? 0x40 : 0) | (_n ? 0x80 : 0));

    private void SetPsw(byte data)
    {
        _c = (data & 0x01) != 0;
        _z = (data & 0x02) != 0;
        _i = (data & 0x04) != 0;
        _h = (data & 0x08) != 0;
        _b = (data & 0x10) != 0;
        _p = (data & 0x20) != 0;
        _v = (data & 0x40) != 0;
        _n = (data & 0x80) != 0;
    }

    // ----- Memory helpers ------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte Fetch() => Read(_pc++);
    private int FetchWord() { int lo = Fetch(); return lo | Fetch() << 8; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte Load(int address) => Read((_p ? 0x100 : 0) | (address & 0xff));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Store(int address, byte data) => Write((_p ? 0x100 : 0) | (address & 0xff), data);
    private byte Pull() => Read(0x100 | ++_s);
    private void Push(int data) => Write(0x100 | _s--, (byte)data);

    // ----- ALU -----------------------------------------------------------------------------------------

    private byte Adc(int x, int y)
    {
        var z = x + y + (_c ? 1 : 0);
        _c = z > 0xff;
        _z = (byte)z == 0;
        _h = ((x ^ y ^ z) & 0x10) != 0;
        _v = (~(x ^ y) & (x ^ z) & 0x80) != 0;
        _n = (z & 0x80) != 0;
        return (byte)z;
    }

    private byte And(int x, int y) => Nz((byte)(x & y));
    private byte Or(int x, int y) => Nz((byte)(x | y));
    private byte Eor(int x, int y) => Nz((byte)(x ^ y));
    private byte Ld(int x, int y) => Nz((byte)y);
    private byte Sbc(int x, int y) => Adc(x, ~y & 0xff);

    private byte Cmp(int x, int y)
    {
        var z = x - y;
        _c = z >= 0;
        _z = (byte)z == 0;
        _n = (z & 0x80) != 0;
        return (byte)x;
    }

    private byte Asl(int x)
    {
        _c = (x & 0x80) != 0;
        return Nz((byte)(x << 1));
    }

    private byte Lsr(int x)
    {
        _c = (x & 0x01) != 0;
        return Nz((byte)(x >> 1));
    }

    private byte Rol(int x)
    {
        var carry = _c ? 1 : 0;
        _c = (x & 0x80) != 0;
        return Nz((byte)(x << 1 | carry));
    }

    private byte Ror(int x)
    {
        var carry = _c ? 0x80 : 0;
        _c = (x & 0x01) != 0;
        return Nz((byte)(carry | x >> 1));
    }

    private byte Dec(int x) => Nz((byte)(x - 1));
    private byte Inc(int x) => Nz((byte)(x + 1));

    private byte Nz(byte x)
    {
        _z = x == 0;
        _n = (x & 0x80) != 0;
        return x;
    }

    private int Adw(int x, int y)
    {
        _c = false;
        int z = Adc(x & 0xff, y & 0xff);
        z |= Adc(x >> 8, y >> 8) << 8;
        _z = z == 0;
        return z;
    }

    private int Sbw(int x, int y)
    {
        _c = true;
        int z = Sbc(x & 0xff, y & 0xff);
        z |= Sbc(x >> 8, y >> 8) << 8;
        _z = z == 0;
        return z;
    }

    private int Cpw(int x, int y)
    {
        var z = x - y;
        _c = z >= 0;
        _z = (ushort)z == 0;
        _n = (z & 0x8000) != 0;
        return x;
    }

    private int Ldw(int x, int y)
    {
        _z = y == 0;
        _n = (y & 0x8000) != 0;
        return y;
    }

    // ALU operation selectors for the generic instruction forms.
    private const int OpOr = 0, OpAnd = 1, OpEor = 2, OpCmp = 3, OpAdc = 4, OpSbc = 5, OpLd = 6;
    private const int OpAsl = 0, OpRol = 1, OpLsr = 2, OpRor = 3, OpDec = 4, OpInc = 5;

    private byte Alu(int op, int x, int y) => op switch
    {
        OpOr => Or(x, y),
        OpAnd => And(x, y),
        OpEor => Eor(x, y),
        OpCmp => Cmp(x, y),
        OpAdc => Adc(x, y),
        OpSbc => Sbc(x, y),
        _ => Ld(x, y),
    };

    private byte Modify(int op, int x) => op switch
    {
        OpAsl => Asl(x),
        OpRol => Rol(x),
        OpLsr => Lsr(x),
        OpRor => Ror(x),
        OpDec => Dec(x),
        _ => Inc(x),
    };

    // ----- Instruction forms ---------------------------------------------------------------------------

    private void AbsoluteBitModify(int mode)
    {
        var address = FetchWord();
        var bit = address >> 13;
        address &= 0x1fff;
        var data = Read(address);
        var set = ((data >> bit) & 1) != 0;
        switch (mode)
        {
            case 0: Idle(); _c |= set; break;   // or1 c,addr:bit
            case 1: Idle(); _c |= !set; break;  // or1 c,!addr:bit
            case 2: _c &= set; break;           // and1 c,addr:bit
            case 3: _c &= !set; break;          // and1 c,!addr:bit
            case 4: Idle(); _c ^= set; break;   // eor1 c,addr:bit
            case 5: _c = set; break;            // mov1 c,addr:bit
            case 6:                             // mov1 addr:bit,c
                Idle();
                data = (byte)(_c ? data | (1 << bit) : data & ~(1 << bit));
                Write(address, data);
                break;
            case 7:                             // not1 addr:bit
                data ^= (byte)(1 << bit);
                Write(address, data);
                break;
        }
    }

    private void AbsoluteBitSet(int bit, bool value)
    {
        var address = Fetch();
        var data = Load(address);
        data = (byte)(value ? data | (1 << bit) : data & ~(1 << bit));
        Store(address, data);
    }

    private byte AbsoluteRead()
    {
        var address = FetchWord();
        return Read(address);
    }

    private void AbsoluteModify(int op)
    {
        var address = FetchWord();
        var data = Read(address);
        Write(address, Modify(op, data));
    }

    private void AbsoluteWrite(byte data)
    {
        var address = FetchWord();
        Read(address);
        Write(address, data);
    }

    private void AbsoluteIndexedRead(int op, byte index)
    {
        var address = FetchWord();
        Idle();
        var data = Read(address + index);
        _a = Alu(op, _a, data);
    }

    private void AbsoluteIndexedWrite(byte index)
    {
        var address = FetchWord();
        Idle();
        Read(address + index);
        Write(address + index, _a);
    }

    private void Branch(bool take)
    {
        var data = Fetch();
        if (!take) return;
        Idle();
        Idle();
        _pc = (ushort)(_pc + (sbyte)data);
    }

    private void BranchBit(int bit, bool match)
    {
        var address = Fetch();
        var data = Load(address);
        Idle();
        var displacement = Fetch();
        if ((((data >> bit) & 1) != 0) != match) return;
        Idle();
        Idle();
        _pc = (ushort)(_pc + (sbyte)displacement);
    }

    private void BranchNotDirect()
    {
        var address = Fetch();
        var data = Load(address);
        Idle();
        var displacement = Fetch();
        if (_a == data) return;
        Idle();
        Idle();
        _pc = (ushort)(_pc + (sbyte)displacement);
    }

    private void BranchNotDirectDecrement()
    {
        var address = Fetch();
        var data = (byte)(Load(address) - 1);
        Store(address, data);
        var displacement = Fetch();
        if (data == 0) return;
        Idle();
        Idle();
        _pc = (ushort)(_pc + (sbyte)displacement);
    }

    private void BranchNotDirectIndexed(byte index)
    {
        var address = Fetch();
        Idle();
        var data = Load(address + index);
        Idle();
        var displacement = Fetch();
        if (_a == data) return;
        Idle();
        Idle();
        _pc = (ushort)(_pc + (sbyte)displacement);
    }

    private void BranchNotYDecrement()
    {
        Read(_pc);
        Idle();
        var displacement = Fetch();
        if (--_y == 0) return;
        Idle();
        Idle();
        _pc = (ushort)(_pc + (sbyte)displacement);
    }

    private void Break()
    {
        Read(_pc);
        Push(_pc >> 8);
        Push(_pc);
        Push(Psw);
        Idle();
        int address = Read(0xffde);
        address |= Read(0xffdf) << 8;
        _pc = (ushort)address;
        _i = false;
        _b = true;
    }

    private void CallAbsolute()
    {
        var address = FetchWord();
        Idle();
        Push(_pc >> 8);
        Push(_pc);
        Idle();
        Idle();
        _pc = (ushort)address;
    }

    private void CallPage()
    {
        var address = Fetch();
        Idle();
        Push(_pc >> 8);
        Push(_pc);
        Idle();
        _pc = (ushort)(0xff00 | address);
    }

    private void CallTable(int vector)
    {
        Read(_pc);
        Idle();
        Push(_pc >> 8);
        Push(_pc);
        Idle();
        var address = 0xffde - (vector << 1);
        int pc = Read(address);
        pc |= Read(address + 1) << 8;
        _pc = (ushort)pc;
    }

    private void ComplementCarry()
    {
        Read(_pc);
        Idle();
        _c = !_c;
    }

    private void DecimalAdjustAdd()
    {
        Read(_pc);
        Idle();
        if (_c || _a > 0x99)
        {
            _a += 0x60;
            _c = true;
        }
        if (_h || (_a & 15) > 0x09) _a += 0x06;
        Nz(_a);
    }

    private void DecimalAdjustSub()
    {
        Read(_pc);
        Idle();
        if (!_c || _a > 0x99)
        {
            _a -= 0x60;
            _c = false;
        }
        if (!_h || (_a & 15) > 0x09) _a -= 0x06;
        Nz(_a);
    }

    private byte DirectRead()
    {
        var address = Fetch();
        return Load(address);
    }

    private void DirectModify(int op)
    {
        var address = Fetch();
        var data = Load(address);
        Store(address, Modify(op, data));
    }

    private void DirectWrite(byte data)
    {
        var address = Fetch();
        Load(address);
        Store(address, data);
    }

    private void DirectDirectCompare(int op)
    {
        var source = Fetch();
        var rhs = Load(source);
        var target = Fetch();
        var lhs = Load(target);
        Alu(op, lhs, rhs);
        Idle();
    }

    private void DirectDirectModify(int op)
    {
        var source = Fetch();
        var rhs = Load(source);
        var target = Fetch();
        var lhs = Load(target);
        Store(target, Alu(op, lhs, rhs));
    }

    private void DirectDirectWrite()
    {
        var source = Fetch();
        var data = Load(source);
        var target = Fetch();
        Store(target, data);
    }

    private void DirectImmediateCompare(int op)
    {
        var immediate = Fetch();
        var address = Fetch();
        var data = Load(address);
        Alu(op, data, immediate);
        Idle();
    }

    private void DirectImmediateModify(int op)
    {
        var immediate = Fetch();
        var address = Fetch();
        var data = Load(address);
        Store(address, Alu(op, data, immediate));
    }

    private void DirectImmediateWrite()
    {
        var immediate = Fetch();
        var address = Fetch();
        Load(address);
        Store(address, immediate);
    }

    private void DirectCompareWord()
    {
        var address = Fetch();
        int data = Load(address);
        data |= Load(address + 1) << 8;
        Cpw(YA, data);
    }

    private void DirectReadWord(int op)
    {
        var address = Fetch();
        int data = Load(address);
        Idle();
        data |= Load(address + 1) << 8;
        YA = op switch
        {
            OpAdc => Adw(YA, data),
            OpSbc => Sbw(YA, data),
            _ => Ldw(YA, data),
        };
    }

    private void DirectModifyWord(int adjust)
    {
        var address = Fetch();
        var data = (Load(address) + adjust) & 0xffff;
        Store(address, (byte)data);
        data = (data + (Load(address + 1) << 8)) & 0xffff;
        Store(address + 1, (byte)(data >> 8));
        _z = data == 0;
        _n = (data & 0x8000) != 0;
    }

    private void DirectWriteWord()
    {
        var address = Fetch();
        Load(address);
        Store(address, _a);
        Store(address + 1, _y);
    }

    private byte DirectIndexedRead(byte index)
    {
        var address = Fetch();
        Idle();
        return Load(address + index);
    }

    private void DirectIndexedModify(int op, byte index)
    {
        var address = Fetch();
        Idle();
        var data = Load(address + index);
        Store(address + index, Modify(op, data));
    }

    private void DirectIndexedWrite(byte data, byte index)
    {
        var address = Fetch();
        Idle();
        Load(address + index);
        Store(address + index, data);
    }

    private void Divide()
    {
        Read(_pc);
        for (var i = 0; i < 10; i++) Idle();
        var ya = YA;
        // The overflow flag is set when the quotient is >= 256.
        _h = (_y & 15) >= (_x & 15);
        _v = _y >= _x;
        if (_y < (_x << 1))
        {
            // The quotient fits in 9 bits.
            _a = (byte)(ya / _x);
            _y = (byte)(ya % _x);
        }
        else
        {
            // It doesn't: this reproduces the S-SMP's odd result in that case.
            _a = (byte)(255 - (ya - (_x << 9)) / (256 - _x));
            _y = (byte)(_x + (ya - (_x << 9)) % (256 - _x));
        }
        // Flags follow the quotient only.
        Nz(_a);
    }

    private void ExchangeNibble()
    {
        Read(_pc);
        Idle();
        Idle();
        Idle();
        _a = (byte)(_a >> 4 | _a << 4);
        Nz(_a);
    }

    private byte ImmediateRead() => Fetch();

    private byte ImpliedModify(int op, byte target)
    {
        Read(_pc);
        return Modify(op, target);
    }

    private void IndexedIndirectRead(int op, byte index)
    {
        var indirect = Fetch();
        Idle();
        int address = Load(indirect + index);
        address |= Load(indirect + index + 1) << 8;
        var data = Read(address);
        _a = Alu(op, _a, data);
    }

    private void IndexedIndirectWrite(byte data, byte index)
    {
        var indirect = Fetch();
        Idle();
        int address = Load(indirect + index);
        address |= Load(indirect + index + 1) << 8;
        Read(address);
        Write(address, data);
    }

    private void IndirectIndexedRead(int op, byte index)
    {
        var indirect = Fetch();
        Idle();
        int address = Load(indirect);
        address |= Load(indirect + 1) << 8;
        var data = Read(address + index);
        _a = Alu(op, _a, data);
    }

    private void IndirectIndexedWrite(byte data, byte index)
    {
        var indirect = Fetch();
        int address = Load(indirect);
        address |= Load(indirect + 1) << 8;
        Idle();
        Read(address + index);
        Write(address + index, data);
    }

    private void IndirectXRead(int op)
    {
        Read(_pc);
        var data = Load(_x);
        _a = Alu(op, _a, data);
    }

    private void IndirectXWrite(byte data)
    {
        Read(_pc);
        Load(_x);
        Store(_x, data);
    }

    private void IndirectXIncrementRead()
    {
        Read(_pc);
        _a = Load(_x++);
        Idle();  // quirk: an extra idle cycle compared with most read instructions
        Nz(_a);
    }

    private void IndirectXIncrementWrite(byte data)
    {
        Read(_pc);
        Idle();  // quirk: not a read cycle as with most write instructions
        Store(_x++, data);
    }

    private void IndirectXCompareIndirectY(int op)
    {
        Read(_pc);
        var rhs = Load(_y);
        var lhs = Load(_x);
        Alu(op, lhs, rhs);
        Idle();
    }

    private void IndirectXWriteIndirectY(int op)
    {
        Read(_pc);
        var rhs = Load(_y);
        var lhs = Load(_x);
        Store(_x, Alu(op, lhs, rhs));
    }

    private void JumpAbsolute() => _pc = (ushort)FetchWord();

    private void JumpIndirectX()
    {
        var address = FetchWord();
        Idle();
        int pc = Read(address + _x);
        pc |= Read(address + _x + 1) << 8;
        _pc = (ushort)pc;
    }

    private void Multiply()
    {
        Read(_pc);
        for (var i = 0; i < 7; i++) Idle();
        var ya = _y * _a;
        _a = (byte)ya;
        _y = (byte)(ya >> 8);
        // Flags follow the high byte only.
        Nz(_y);
    }

    private void NoOperation() => Read(_pc);

    private void OverflowClear()
    {
        Read(_pc);
        _h = false;
        _v = false;
    }

    private byte PullRegister()
    {
        Read(_pc);
        Idle();
        return Pull();
    }

    private void PullP()
    {
        Read(_pc);
        Idle();
        SetPsw(Pull());
    }

    private void PushRegister(byte data)
    {
        Read(_pc);
        Push(data);
        Idle();
    }

    private void ReturnInterrupt()
    {
        Read(_pc);
        Idle();
        SetPsw(Pull());
        int address = Pull();
        address |= Pull() << 8;
        _pc = (ushort)address;
    }

    private void ReturnSubroutine()
    {
        Read(_pc);
        Idle();
        int address = Pull();
        address |= Pull() << 8;
        _pc = (ushort)address;
    }

    private void TestSetBitsAbsolute(bool set)
    {
        var address = FetchWord();
        var data = Read(address);
        var difference = _a - data;
        _z = (byte)difference == 0;
        _n = (difference & 0x80) != 0;
        Read(address);
        Write(address, (byte)(set ? data | _a : data & ~_a));
    }

    private byte Transfer(byte from)
    {
        Read(_pc);
        return Nz(from);
    }

    // ----- Dispatch ------------------------------------------------------------------------------------

    /// <summary>Executes one instruction (or, while sleeping or stopped, one idle loop of the core).</summary>
    public void Step()
    {
        if (_wait || _stop)
        {
            // WAI and STOP keep the bus busy until a reset; there are no interrupts to wake the core.
            Read(_pc);
            Idle();
            return;
        }

        TraceHook?.Invoke(this);
        switch (Fetch())
        {
            case 0x00: NoOperation(); break;
            case 0x01: CallTable(0); break;
            case 0x02: AbsoluteBitSet(0, true); break;
            case 0x03: BranchBit(0, true); break;
            case 0x04: _a = Or(_a, DirectRead()); break;
            case 0x05: _a = Or(_a, AbsoluteRead()); break;
            case 0x06: IndirectXRead(OpOr); break;
            case 0x07: IndexedIndirectRead(OpOr, _x); break;
            case 0x08: _a = Or(_a, ImmediateRead()); break;
            case 0x09: DirectDirectModify(OpOr); break;
            case 0x0a: AbsoluteBitModify(0); break;
            case 0x0b: DirectModify(OpAsl); break;
            case 0x0c: AbsoluteModify(OpAsl); break;
            case 0x0d: PushRegister(Psw); break;
            case 0x0e: TestSetBitsAbsolute(true); break;
            case 0x0f: Break(); break;
            case 0x10: Branch(!_n); break;
            case 0x11: CallTable(1); break;
            case 0x12: AbsoluteBitSet(0, false); break;
            case 0x13: BranchBit(0, false); break;
            case 0x14: _a = Or(_a, DirectIndexedRead(_x)); break;
            case 0x15: AbsoluteIndexedRead(OpOr, _x); break;
            case 0x16: AbsoluteIndexedRead(OpOr, _y); break;
            case 0x17: IndirectIndexedRead(OpOr, _y); break;
            case 0x18: DirectImmediateModify(OpOr); break;
            case 0x19: IndirectXWriteIndirectY(OpOr); break;
            case 0x1a: DirectModifyWord(-1); break;
            case 0x1b: DirectIndexedModify(OpAsl, _x); break;
            case 0x1c: _a = ImpliedModify(OpAsl, _a); break;
            case 0x1d: _x = ImpliedModify(OpDec, _x); break;
            case 0x1e: _x = Cmp(_x, AbsoluteRead()); break;
            case 0x1f: JumpIndirectX(); break;
            case 0x20: Read(_pc); _p = false; break;
            case 0x21: CallTable(2); break;
            case 0x22: AbsoluteBitSet(1, true); break;
            case 0x23: BranchBit(1, true); break;
            case 0x24: _a = And(_a, DirectRead()); break;
            case 0x25: _a = And(_a, AbsoluteRead()); break;
            case 0x26: IndirectXRead(OpAnd); break;
            case 0x27: IndexedIndirectRead(OpAnd, _x); break;
            case 0x28: _a = And(_a, ImmediateRead()); break;
            case 0x29: DirectDirectModify(OpAnd); break;
            case 0x2a: AbsoluteBitModify(1); break;
            case 0x2b: DirectModify(OpRol); break;
            case 0x2c: AbsoluteModify(OpRol); break;
            case 0x2d: PushRegister(_a); break;
            case 0x2e: BranchNotDirect(); break;
            case 0x2f: Branch(true); break;
            case 0x30: Branch(_n); break;
            case 0x31: CallTable(3); break;
            case 0x32: AbsoluteBitSet(1, false); break;
            case 0x33: BranchBit(1, false); break;
            case 0x34: _a = And(_a, DirectIndexedRead(_x)); break;
            case 0x35: AbsoluteIndexedRead(OpAnd, _x); break;
            case 0x36: AbsoluteIndexedRead(OpAnd, _y); break;
            case 0x37: IndirectIndexedRead(OpAnd, _y); break;
            case 0x38: DirectImmediateModify(OpAnd); break;
            case 0x39: IndirectXWriteIndirectY(OpAnd); break;
            case 0x3a: DirectModifyWord(+1); break;
            case 0x3b: DirectIndexedModify(OpRol, _x); break;
            case 0x3c: _a = ImpliedModify(OpRol, _a); break;
            case 0x3d: _x = ImpliedModify(OpInc, _x); break;
            case 0x3e: _x = Cmp(_x, DirectRead()); break;
            case 0x3f: CallAbsolute(); break;
            case 0x40: Read(_pc); _p = true; break;
            case 0x41: CallTable(4); break;
            case 0x42: AbsoluteBitSet(2, true); break;
            case 0x43: BranchBit(2, true); break;
            case 0x44: _a = Eor(_a, DirectRead()); break;
            case 0x45: _a = Eor(_a, AbsoluteRead()); break;
            case 0x46: IndirectXRead(OpEor); break;
            case 0x47: IndexedIndirectRead(OpEor, _x); break;
            case 0x48: _a = Eor(_a, ImmediateRead()); break;
            case 0x49: DirectDirectModify(OpEor); break;
            case 0x4a: AbsoluteBitModify(2); break;
            case 0x4b: DirectModify(OpLsr); break;
            case 0x4c: AbsoluteModify(OpLsr); break;
            case 0x4d: PushRegister(_x); break;
            case 0x4e: TestSetBitsAbsolute(false); break;
            case 0x4f: CallPage(); break;
            case 0x50: Branch(!_v); break;
            case 0x51: CallTable(5); break;
            case 0x52: AbsoluteBitSet(2, false); break;
            case 0x53: BranchBit(2, false); break;
            case 0x54: _a = Eor(_a, DirectIndexedRead(_x)); break;
            case 0x55: AbsoluteIndexedRead(OpEor, _x); break;
            case 0x56: AbsoluteIndexedRead(OpEor, _y); break;
            case 0x57: IndirectIndexedRead(OpEor, _y); break;
            case 0x58: DirectImmediateModify(OpEor); break;
            case 0x59: IndirectXWriteIndirectY(OpEor); break;
            case 0x5a: DirectCompareWord(); break;
            case 0x5b: DirectIndexedModify(OpLsr, _x); break;
            case 0x5c: _a = ImpliedModify(OpLsr, _a); break;
            case 0x5d: _x = Transfer(_a); break;
            case 0x5e: _y = Cmp(_y, AbsoluteRead()); break;
            case 0x5f: JumpAbsolute(); break;
            case 0x60: Read(_pc); _c = false; break;
            case 0x61: CallTable(6); break;
            case 0x62: AbsoluteBitSet(3, true); break;
            case 0x63: BranchBit(3, true); break;
            case 0x64: _a = Cmp(_a, DirectRead()); break;
            case 0x65: _a = Cmp(_a, AbsoluteRead()); break;
            case 0x66: IndirectXRead(OpCmp); break;
            case 0x67: IndexedIndirectRead(OpCmp, _x); break;
            case 0x68: _a = Cmp(_a, ImmediateRead()); break;
            case 0x69: DirectDirectCompare(OpCmp); break;
            case 0x6a: AbsoluteBitModify(3); break;
            case 0x6b: DirectModify(OpRor); break;
            case 0x6c: AbsoluteModify(OpRor); break;
            case 0x6d: PushRegister(_y); break;
            case 0x6e: BranchNotDirectDecrement(); break;
            case 0x6f: ReturnSubroutine(); break;
            case 0x70: Branch(_v); break;
            case 0x71: CallTable(7); break;
            case 0x72: AbsoluteBitSet(3, false); break;
            case 0x73: BranchBit(3, false); break;
            case 0x74: _a = Cmp(_a, DirectIndexedRead(_x)); break;
            case 0x75: AbsoluteIndexedRead(OpCmp, _x); break;
            case 0x76: AbsoluteIndexedRead(OpCmp, _y); break;
            case 0x77: IndirectIndexedRead(OpCmp, _y); break;
            case 0x78: DirectImmediateCompare(OpCmp); break;
            case 0x79: IndirectXCompareIndirectY(OpCmp); break;
            case 0x7a: DirectReadWord(OpAdc); break;
            case 0x7b: DirectIndexedModify(OpRor, _x); break;
            case 0x7c: _a = ImpliedModify(OpRor, _a); break;
            case 0x7d: _a = Transfer(_x); break;
            case 0x7e: _y = Cmp(_y, DirectRead()); break;
            case 0x7f: ReturnInterrupt(); break;
            case 0x80: Read(_pc); _c = true; break;
            case 0x81: CallTable(8); break;
            case 0x82: AbsoluteBitSet(4, true); break;
            case 0x83: BranchBit(4, true); break;
            case 0x84: _a = Adc(_a, DirectRead()); break;
            case 0x85: _a = Adc(_a, AbsoluteRead()); break;
            case 0x86: IndirectXRead(OpAdc); break;
            case 0x87: IndexedIndirectRead(OpAdc, _x); break;
            case 0x88: _a = Adc(_a, ImmediateRead()); break;
            case 0x89: DirectDirectModify(OpAdc); break;
            case 0x8a: AbsoluteBitModify(4); break;
            case 0x8b: DirectModify(OpDec); break;
            case 0x8c: AbsoluteModify(OpDec); break;
            case 0x8d: _y = Ld(_y, ImmediateRead()); break;
            case 0x8e: PullP(); break;
            case 0x8f: DirectImmediateWrite(); break;
            case 0x90: Branch(!_c); break;
            case 0x91: CallTable(9); break;
            case 0x92: AbsoluteBitSet(4, false); break;
            case 0x93: BranchBit(4, false); break;
            case 0x94: _a = Adc(_a, DirectIndexedRead(_x)); break;
            case 0x95: AbsoluteIndexedRead(OpAdc, _x); break;
            case 0x96: AbsoluteIndexedRead(OpAdc, _y); break;
            case 0x97: IndirectIndexedRead(OpAdc, _y); break;
            case 0x98: DirectImmediateModify(OpAdc); break;
            case 0x99: IndirectXWriteIndirectY(OpAdc); break;
            case 0x9a: DirectReadWord(OpSbc); break;
            case 0x9b: DirectIndexedModify(OpDec, _x); break;
            case 0x9c: _a = ImpliedModify(OpDec, _a); break;
            case 0x9d: _x = Transfer(_s); break;
            case 0x9e: Divide(); break;
            case 0x9f: ExchangeNibble(); break;
            case 0xa0: Read(_pc); Idle(); _i = true; break;
            case 0xa1: CallTable(10); break;
            case 0xa2: AbsoluteBitSet(5, true); break;
            case 0xa3: BranchBit(5, true); break;
            case 0xa4: _a = Sbc(_a, DirectRead()); break;
            case 0xa5: _a = Sbc(_a, AbsoluteRead()); break;
            case 0xa6: IndirectXRead(OpSbc); break;
            case 0xa7: IndexedIndirectRead(OpSbc, _x); break;
            case 0xa8: _a = Sbc(_a, ImmediateRead()); break;
            case 0xa9: DirectDirectModify(OpSbc); break;
            case 0xaa: AbsoluteBitModify(5); break;
            case 0xab: DirectModify(OpInc); break;
            case 0xac: AbsoluteModify(OpInc); break;
            case 0xad: _y = Cmp(_y, ImmediateRead()); break;
            case 0xae: _a = PullRegister(); break;
            case 0xaf: IndirectXIncrementWrite(_a); break;
            case 0xb0: Branch(_c); break;
            case 0xb1: CallTable(11); break;
            case 0xb2: AbsoluteBitSet(5, false); break;
            case 0xb3: BranchBit(5, false); break;
            case 0xb4: _a = Sbc(_a, DirectIndexedRead(_x)); break;
            case 0xb5: AbsoluteIndexedRead(OpSbc, _x); break;
            case 0xb6: AbsoluteIndexedRead(OpSbc, _y); break;
            case 0xb7: IndirectIndexedRead(OpSbc, _y); break;
            case 0xb8: DirectImmediateModify(OpSbc); break;
            case 0xb9: IndirectXWriteIndirectY(OpSbc); break;
            case 0xba: DirectReadWord(OpLd); break;
            case 0xbb: DirectIndexedModify(OpInc, _x); break;
            case 0xbc: _a = ImpliedModify(OpInc, _a); break;
            case 0xbd: Read(_pc); _s = _x; break;  // mov sp,x leaves the flags alone
            case 0xbe: DecimalAdjustSub(); break;
            case 0xbf: IndirectXIncrementRead(); break;
            case 0xc0: Read(_pc); Idle(); _i = false; break;
            case 0xc1: CallTable(12); break;
            case 0xc2: AbsoluteBitSet(6, true); break;
            case 0xc3: BranchBit(6, true); break;
            case 0xc4: DirectWrite(_a); break;
            case 0xc5: AbsoluteWrite(_a); break;
            case 0xc6: IndirectXWrite(_a); break;
            case 0xc7: IndexedIndirectWrite(_a, _x); break;
            case 0xc8: _x = Cmp(_x, ImmediateRead()); break;
            case 0xc9: AbsoluteWrite(_x); break;
            case 0xca: AbsoluteBitModify(6); break;
            case 0xcb: DirectWrite(_y); break;
            case 0xcc: AbsoluteWrite(_y); break;
            case 0xcd: _x = Ld(_x, ImmediateRead()); break;
            case 0xce: _x = PullRegister(); break;
            case 0xcf: Multiply(); break;
            case 0xd0: Branch(!_z); break;
            case 0xd1: CallTable(13); break;
            case 0xd2: AbsoluteBitSet(6, false); break;
            case 0xd3: BranchBit(6, false); break;
            case 0xd4: DirectIndexedWrite(_a, _x); break;
            case 0xd5: AbsoluteIndexedWrite(_x); break;
            case 0xd6: AbsoluteIndexedWrite(_y); break;
            case 0xd7: IndirectIndexedWrite(_a, _y); break;
            case 0xd8: DirectWrite(_x); break;
            case 0xd9: DirectIndexedWrite(_x, _y); break;
            case 0xda: DirectWriteWord(); break;
            case 0xdb: DirectIndexedWrite(_y, _x); break;
            case 0xdc: _y = ImpliedModify(OpDec, _y); break;
            case 0xdd: _a = Transfer(_y); break;
            case 0xde: BranchNotDirectIndexed(_x); break;
            case 0xdf: DecimalAdjustAdd(); break;
            case 0xe0: OverflowClear(); break;
            case 0xe1: CallTable(14); break;
            case 0xe2: AbsoluteBitSet(7, true); break;
            case 0xe3: BranchBit(7, true); break;
            case 0xe4: _a = Ld(_a, DirectRead()); break;
            case 0xe5: _a = Ld(_a, AbsoluteRead()); break;
            case 0xe6: IndirectXRead(OpLd); break;
            case 0xe7: IndexedIndirectRead(OpLd, _x); break;
            case 0xe8: _a = Ld(_a, ImmediateRead()); break;
            case 0xe9: _x = Ld(_x, AbsoluteRead()); break;
            case 0xea: AbsoluteBitModify(7); break;
            case 0xeb: _y = Ld(_y, DirectRead()); break;
            case 0xec: _y = Ld(_y, AbsoluteRead()); break;
            case 0xed: ComplementCarry(); break;
            case 0xee: _y = PullRegister(); break;
            case 0xef: _wait = true; Read(_pc); Idle(); break;
            case 0xf0: Branch(_z); break;
            case 0xf1: CallTable(15); break;
            case 0xf2: AbsoluteBitSet(7, false); break;
            case 0xf3: BranchBit(7, false); break;
            case 0xf4: _a = Ld(_a, DirectIndexedRead(_x)); break;
            case 0xf5: AbsoluteIndexedRead(OpLd, _x); break;
            case 0xf6: AbsoluteIndexedRead(OpLd, _y); break;
            case 0xf7: IndirectIndexedRead(OpLd, _y); break;
            case 0xf8: _x = Ld(_x, DirectRead()); break;
            case 0xf9: _x = Ld(_x, DirectIndexedRead(_y)); break;
            case 0xfa: DirectDirectWrite(); break;
            case 0xfb: _y = Ld(_y, DirectIndexedRead(_x)); break;
            case 0xfc: _y = ImpliedModify(OpInc, _y); break;
            case 0xfd: _y = Transfer(_a); break;
            case 0xfe: BranchNotYDecrement(); break;
            case 0xff: _stop = true; Read(_pc); Idle(); break;
        }
    }
}
