b00 = [23, 20, 2, 13, 7, 22, 10, 41, 6, 9, 11, 12, 4, 15, 33, 28, 21, 18, 19, 1, 39, 31, 27, 25, 5, 34, 40, 14, 24, 29, 35, 3, 16, 17, 36, 32, 38, 45, 26, 30, 46, 43, 44]
print("b00 len:", len(b00))
print("unique in b00:", len(set(b00)))
missing = [i for i in range(1, 47) if i not in b00]
print("missing from 1..46:", missing)
