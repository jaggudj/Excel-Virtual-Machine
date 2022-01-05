#include <stdio.h>
#include <conio.h>
void main()
{
    char str[10];
    int i, n;
    printf("enter number of charcters in a string\n");
    scanf("%d", &n);
    printf("enter string: charcter by charcter\n");
    fflush(stdin);
    for (i=0;i<n;i++)
    {
        str[i] = getchar();
        fflush(stdin);
    }
    str[i] = '\0';
    printf("displaying string: charcter by charcter\n");
    for (i = 0; i, n; i++)
    {
        putchar(str[i]);
    }
    getchar();
}
